using System;

namespace ParityProof.Core.Models;

public sealed record ExifMetadataInfo(
    string? CameraMake = null,
    string? CameraModel = null,
    string? LensModel = null,
    string? DateTimeOriginal = null,
    string? Dimensions = null,
    string? Iso = null,
    string? ExposureTime = null,
    string? FNumber = null,
    string? FocalLength = null,
    bool HasExif = false)
{
    public static readonly ExifMetadataInfo Empty = new();

    public string FormattedExposureSummary
    {
        get
        {
            if (!HasExif)
            {
                return "No EXIF exposure metadata available";
            }

            System.Collections.Generic.List<string> parts = new();
            if (!string.IsNullOrWhiteSpace(FocalLength))
            {
                parts.Add(FocalLength);
            }

            if (!string.IsNullOrWhiteSpace(FNumber))
            {
                parts.Add(FNumber);
            }

            if (!string.IsNullOrWhiteSpace(ExposureTime))
            {
                parts.Add(ExposureTime);
            }

            if (!string.IsNullOrWhiteSpace(Iso))
            {
                parts.Add(Iso);
            }

            return parts.Count > 0 ? string.Join(" • ", parts) : "Metadata present";
        }
    }

    public string FormattedCameraSummary
    {
        get
        {
            if (!HasExif)
            {
                return "Unknown Camera";
            }

            if (!string.IsNullOrWhiteSpace(CameraModel))
            {
                if (!string.IsNullOrWhiteSpace(CameraMake) &&
                    !CameraModel.StartsWith(CameraMake, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{CameraMake} {CameraModel}";
                }

                return CameraModel;
            }

            return CameraMake ?? "Unknown Camera";
        }
    }
}
