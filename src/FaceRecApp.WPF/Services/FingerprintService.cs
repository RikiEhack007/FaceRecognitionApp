using System.Windows.Media;
using System.Windows.Media.Imaging;
using libzkfpcsharp;

namespace FaceRecApp.WPF.Services;

/// <summary>
/// Wraps the ZKFinger SDK (ZK9500 fingerprint scanner) into an event-driven service.
///
/// Design follows the same patterns as CameraService:
///   - Singleton in DI (scanner connection persists like camera)
///   - Background capture thread polls device every 200ms
///   - Events fire to UI for capture notifications
///   - In-memory DB from SDK handles fingerprint matching
///
/// SDK flow: Init → OpenDevice → DBInit → capture thread → AcquireFingerprint
///           → DBIdentify (1:N) or DBMatch (1:1)
///
/// Enrollment: 3 captures of same finger → DBMerge → merged template → store in DB
/// </summary>
public class FingerprintService : IDisposable
{
    private IntPtr _deviceHandle = IntPtr.Zero;
    private IntPtr _dbHandle = IntPtr.Zero;
    private bool _isInitialized;
    private bool _disposed;

    private int _imageWidth;
    private int _imageHeight;
    private byte[]? _imageBuffer;
    private readonly byte[] _capTemplate = new byte[2048];
    private int _cbCapTemplate = 2048;

    private Thread? _captureThread;
    private volatile bool _stopCapture = true;
    private int _cacheCount;

    // ─── Public State ───

    public bool IsInitialized => _isInitialized;
    public bool IsDeviceOpen => _deviceHandle != IntPtr.Zero;
    public bool IsCapturing => !_stopCapture;
    public int ImageWidth => _imageWidth;
    public int ImageHeight => _imageHeight;
    public int CacheCount => _cacheCount;

    // ─── Events ───

    /// <summary>Fired when a fingerprint is captured from the scanner.</summary>
    public event EventHandler<FingerprintCapturedEventArgs>? FingerprintCaptured;

    /// <summary>Fired on SDK errors.</summary>
    public event EventHandler<string>? Error;

    // ══════════════════════════════════════════════
    //  LIFECYCLE
    // ══════════════════════════════════════════════

    /// <summary>
    /// Initialize the ZK fingerprint library.
    /// Returns the number of connected devices (0 = none found, -1 = SDK load failed).
    /// </summary>
    public int Initialize()
    {
        if (_isInitialized)
        {
            return zkfp2.GetDeviceCount();
        }

        try
        {
            int ret = zkfp2.Init();
            // ZKFP_ERR_ALREADY_INIT (1) is also acceptable — SDK was already initialized externally
            if (ret != zkfperrdef.ZKFP_ERR_OK && ret != zkfp.ZKFP_ERR_ALREADY_INIT)
            {
                Error?.Invoke(this, $"SDK init failed (code {ret})");
                return 0;
            }

            _isInitialized = true;
            int count = zkfp2.GetDeviceCount();
            Console.WriteLine($"[Fingerprint] Init OK, {count} device(s) found");
            return count;
        }
        catch (DllNotFoundException ex)
        {
            Error?.Invoke(this, $"Fingerprint SDK native DLL not found: {ex.Message}. Ensure libzkfp.dll is in the application directory.");
            Console.WriteLine($"[Fingerprint] FATAL: {ex.Message}");
            return -1;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Fingerprint SDK init exception: {ex.Message}");
            Console.WriteLine($"[Fingerprint] Init exception: {ex}");
            return -1;
        }
    }

    /// <summary>
    /// Open a fingerprint device by index, initialize the matching DB, and start the capture thread.
    /// </summary>
    public bool OpenDevice(int index = 0)
    {
        if (!_isInitialized)
        {
            Error?.Invoke(this, "SDK not initialized. Call Initialize() first.");
            return false;
        }

        if (IsDeviceOpen)
        {
            Console.WriteLine("[Fingerprint] Device already open");
            return true;
        }

        _deviceHandle = zkfp2.OpenDevice(index);
        if (_deviceHandle == IntPtr.Zero)
        {
            Error?.Invoke(this, $"Failed to open device {index}");
            return false;
        }

        _dbHandle = zkfp2.DBInit();
        if (_dbHandle == IntPtr.Zero)
        {
            Error?.Invoke(this, "Failed to init matching DB");
            zkfp2.CloseDevice(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
            return false;
        }

        // Query image dimensions
        byte[] paramValue = new byte[4];
        int size = 4;
        zkfp2.GetParameters(_deviceHandle, 1, paramValue, ref size);
        zkfp2.ByteArray2Int(paramValue, ref _imageWidth);

        size = 4;
        zkfp2.GetParameters(_deviceHandle, 2, paramValue, ref size);
        zkfp2.ByteArray2Int(paramValue, ref _imageHeight);

        _imageBuffer = new byte[_imageWidth * _imageHeight];

        // Start capture thread
        StartCapture();

        Console.WriteLine($"[Fingerprint] Device opened (image: {_imageWidth}x{_imageHeight})");
        return true;
    }

    /// <summary>Close the device and stop capture.</summary>
    public void CloseDevice()
    {
        if (!IsDeviceOpen) return;

        StopCapture();

        if (_dbHandle != IntPtr.Zero)
        {
            zkfp2.DBFree(_dbHandle);
            _dbHandle = IntPtr.Zero;
        }

        zkfp2.CloseDevice(_deviceHandle);
        _deviceHandle = IntPtr.Zero;
        _cacheCount = 0;

        Console.WriteLine("[Fingerprint] Device closed");
    }

    /// <summary>Terminate the SDK library.</summary>
    public void Terminate()
    {
        CloseDevice();

        if (_isInitialized)
        {
            zkfp2.Terminate();
            _isInitialized = false;
            Console.WriteLine("[Fingerprint] SDK terminated");
        }
    }

    // ══════════════════════════════════════════════
    //  CAPTURE THREAD
    // ══════════════════════════════════════════════

    /// <summary>Start the background capture thread (polls every 200ms).</summary>
    public void StartCapture()
    {
        if (!IsDeviceOpen || !_stopCapture) return;

        _stopCapture = false;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "FingerprintCapture"
        };
        _captureThread.Start();
    }

    /// <summary>Stop the capture thread and wait for it to finish.</summary>
    public void StopCapture()
    {
        if (_stopCapture) return;

        _stopCapture = true;
        _captureThread?.Join(2000);
        _captureThread = null;
    }

    private void CaptureLoop()
    {
        while (!_stopCapture)
        {
            if (_imageBuffer == null) break;

            _cbCapTemplate = 2048;
            int ret = zkfp2.AcquireFingerprint(_deviceHandle, _imageBuffer, _capTemplate, ref _cbCapTemplate);

            if (ret == zkfp.ZKFP_ERR_OK)
            {
                // Copy buffers before firing event (thread safety)
                var template = new byte[_cbCapTemplate];
                Array.Copy(_capTemplate, template, _cbCapTemplate);

                var image = new byte[_imageBuffer.Length];
                Array.Copy(_imageBuffer, image, _imageBuffer.Length);

                FingerprintCaptured?.Invoke(this, new FingerprintCapturedEventArgs(
                    template, _cbCapTemplate, image));
            }

            Thread.Sleep(200);
        }
    }

    // ══════════════════════════════════════════════
    //  TEMPLATE CACHE (for 1:N matching)
    // ══════════════════════════════════════════════

    /// <summary>Clear all templates from the in-memory matching cache.</summary>
    public void ClearCache()
    {
        if (_dbHandle == IntPtr.Zero) return;
        zkfp2.DBClear(_dbHandle);
        _cacheCount = 0;
    }

    /// <summary>
    /// Load fingerprint templates into the SDK's in-memory cache for 1:N matching.
    /// Key = FID (FingerprintTemplate.Id from SQL), Value = merged template bytes.
    /// </summary>
    public void LoadTemplates(Dictionary<int, byte[]> fidToTemplate)
    {
        if (_dbHandle == IntPtr.Zero) return;

        ClearCache();

        foreach (var (fid, template) in fidToTemplate)
        {
            int ret = zkfp2.DBAdd(_dbHandle, fid, template);
            if (ret == zkfp.ZKFP_ERR_OK)
                _cacheCount++;
            else
                Console.WriteLine($"[Fingerprint] DBAdd failed for fid={fid}, ret={ret}");
        }

        Console.WriteLine($"[Fingerprint] Loaded {_cacheCount} template(s) into cache");
    }

    // ══════════════════════════════════════════════
    //  MATCHING
    // ══════════════════════════════════════════════

    /// <summary>
    /// 1:N identification — find the closest match in the cache.
    /// Returns null if no match found.
    /// </summary>
    public FingerprintMatchResult? Identify(byte[] template)
    {
        if (_dbHandle == IntPtr.Zero) return null;

        int fid = 0;
        int score = 0;
        int ret = zkfp2.DBIdentify(_dbHandle, template, ref fid, ref score);

        if (ret == zkfp.ZKFP_ERR_OK)
        {
            Console.WriteLine($"[Fingerprint] Identify: fid={fid}, score={score}");
            return new FingerprintMatchResult(fid, score);
        }

        Console.WriteLine($"[Fingerprint] Identify: no match (ret={ret})");
        return null;
    }

    /// <summary>
    /// 1:1 verification — compare two templates.
    /// Returns match score (> 0 = match), or 0 if no match.
    /// </summary>
    public int Match(byte[] template1, byte[] template2)
    {
        if (_dbHandle == IntPtr.Zero) return 0;

        int score = zkfp2.DBMatch(_dbHandle, template1, template2);
        Console.WriteLine($"[Fingerprint] Match score: {score}");
        return score;
    }

    // ══════════════════════════════════════════════
    //  ENROLLMENT
    // ══════════════════════════════════════════════

    /// <summary>
    /// Merge 3 captured templates into a single enrollment template.
    /// Returns the merged template, or null on failure.
    /// </summary>
    public byte[]? MergeTemplates(byte[] t1, byte[] t2, byte[] t3)
    {
        if (_dbHandle == IntPtr.Zero) return null;

        byte[] merged = new byte[2048];
        int cbMerged = 2048;

        int ret = zkfp2.DBMerge(_dbHandle, t1, t2, t3, merged, ref cbMerged);
        if (ret == zkfp.ZKFP_ERR_OK)
        {
            // Trim to actual size
            var result = new byte[cbMerged];
            Array.Copy(merged, result, cbMerged);
            Console.WriteLine($"[Fingerprint] Merge OK, size={cbMerged}");
            return result;
        }

        Console.WriteLine($"[Fingerprint] Merge failed, ret={ret}");
        return null;
    }

    // ══════════════════════════════════════════════
    //  IMAGE CONVERSION
    // ══════════════════════════════════════════════

    /// <summary>
    /// Convert raw grayscale fingerprint image to WPF BitmapSource for display.
    /// </summary>
    public static BitmapSource RawToBitmapSource(byte[] rawData, int width, int height)
    {
        var bitmap = BitmapSource.Create(
            width, height, 96, 96,
            PixelFormats.Gray8, null,
            rawData, width);
        bitmap.Freeze();
        return bitmap;
    }

    // ══════════════════════════════════════════════
    //  CLEANUP
    // ══════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Terminate();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Event args for a captured fingerprint.</summary>
public class FingerprintCapturedEventArgs : EventArgs
{
    public byte[] Template { get; }
    public int TemplateSize { get; }
    public byte[] ImageData { get; }

    public FingerprintCapturedEventArgs(byte[] template, int templateSize, byte[] imageData)
    {
        Template = template;
        TemplateSize = templateSize;
        ImageData = imageData;
    }
}

/// <summary>Result of a 1:N fingerprint match.</summary>
public class FingerprintMatchResult
{
    public int Fid { get; }
    public int Score { get; }

    public FingerprintMatchResult(int fid, int score)
    {
        Fid = fid;
        Score = score;
    }
}
