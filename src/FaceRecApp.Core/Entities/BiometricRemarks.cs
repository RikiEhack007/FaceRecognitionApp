namespace FaceRecApp.Core.Entities;

/// <summary>
/// Constants for dropdown options used across the patient identification system.
/// </summary>
public static class BiometricRemarks
{
    /// <summary>
    /// Reasons why fingerprint biometric couldn't be captured.
    /// </summary>
    public static readonly string[] FingerprintRemarks =
    [
        "Physical Deformity",
        "Occupational Wear",
        "Temporary Injury",
        "Skin Condition",
        "Elderly/Thin Skin",
        "Equipment Issue",
        "Patient Refusal"
    ];

    /// <summary>
    /// Reasons why face biometric couldn't be captured.
    /// </summary>
    public static readonly string[] FaceRemarks =
    [
        "Severe Facial Trauma",
        "Post-Surgery (Bandage)",
        "Medical Equipment",
        "Uncooperative"
    ];

    /// <summary>
    /// Available service types for visit routing.
    /// </summary>
    public static readonly string[] ServiceTypes =
    [
        "OPD",
        "ANC",
        "Vaccine",
        "Study",
        "Follow Up"
    ];

    /// <summary>
    /// Sex options: byte value → display name.
    /// </summary>
    public static readonly Dictionary<byte, string> SexOptions = new()
    {
        { 1, "Male" },
        { 2, "Female" }
    };

    /// <summary>
    /// Biometric type identifiers.
    /// </summary>
    public static class Types
    {
        public const string Face = "Face";
        public const string FingerL1 = "FingerL1";
        public const string FingerL2 = "FingerL2";
        public const string FingerL3 = "FingerL3";
        public const string FingerL4 = "FingerL4";
        public const string FingerL5 = "FingerL5";
        public const string FingerR1 = "FingerR1";
        public const string FingerR2 = "FingerR2";
        public const string FingerR3 = "FingerR3";
        public const string FingerR4 = "FingerR4";
        public const string FingerR5 = "FingerR5";
    }
}
