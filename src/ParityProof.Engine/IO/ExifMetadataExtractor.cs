using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using ParityProof.Core.Models;

namespace ParityProof.Engine.IO;

public static class ExifMetadataExtractor
{
    private const int MAX_HEADER_READ_BYTES = 256 * 1024; // 256 KB header sample
    private const ushort TAG_IMAGE_WIDTH = 0x0100;
    private const ushort TAG_IMAGE_HEIGHT = 0x0101;
    private const ushort TAG_MAKE = 0x010F;
    private const ushort TAG_MODEL = 0x0110;
    private const ushort TAG_DATE_TIME = 0x0132;
    private const ushort TAG_EXIF_IFD = 0x8769;
    private const ushort TAG_SUB_IFD = 0x014A;
    private const ushort TAG_JPEG_OFFSET = 0x0201;
    private const ushort TAG_JPEG_LENGTH = 0x0202;

    // Exif SubIFD tags
    private const ushort TAG_EXPOSURE_TIME = 0x829A;
    private const ushort TAG_F_NUMBER = 0x829D;
    private const ushort TAG_ISO = 0x8827;
    private const ushort TAG_DATE_TIME_ORIGINAL = 0x9003;
    private const ushort TAG_FOCAL_LENGTH = 0x920A;
    private const ushort TAG_PIXEL_X_DIMENSION = 0xA002;
    private const ushort TAG_PIXEL_Y_DIMENSION = 0xA003;
    private const ushort TAG_LENS_MODEL = 0xA434;

    public static ExifMetadataInfo ExtractMetadata(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return ExifMetadataInfo.Empty;
        }

        try
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int bytesToRead = (int)Math.Min((long)MAX_HEADER_READ_BYTES, stream.Length);
            if (bytesToRead < 16)
            {
                return ExifMetadataInfo.Empty;
            }

            byte[] header = new byte[bytesToRead];
            int read = stream.Read(header, 0, bytesToRead);
            if (read < 16)
            {
                return ExifMetadataInfo.Empty;
            }

            ReadOnlySpan<byte> span = header.AsSpan(0, read);
            return ParseExifSpan(span);
        }
        catch
        {
            return ExifMetadataInfo.Empty;
        }
    }

    public static byte[]? ExtractThumbnailBytes(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int bytesToRead = (int)Math.Min((long)MAX_HEADER_READ_BYTES, stream.Length);
            if (bytesToRead < 64)
            {
                return null;
            }

            byte[] header = new byte[bytesToRead];
            int read = stream.Read(header, 0, bytesToRead);
            if (read < 64)
            {
                return null;
            }

            ReadOnlySpan<byte> span = header.AsSpan(0, read);
            (int tiffOffset, bool isLittleEndian) = FindTiffHeader(span);
            if (tiffOffset < 0)
            {
                return null;
            }

            (long thumbOffset, int thumbLength) = FindThumbnailOffsets(span, tiffOffset, isLittleEndian);
            if (thumbOffset > 0 && thumbLength > 0 && thumbOffset + thumbLength <= stream.Length)
            {
                byte[] thumbBytes = new byte[thumbLength];
                stream.Seek(thumbOffset, SeekOrigin.Begin);
                int totalRead = 0;
                while (totalRead < thumbLength)
                {
                    int chunkRead = stream.Read(thumbBytes, totalRead, thumbLength - totalRead);
                    if (chunkRead <= 0)
                    {
                        return null;
                    }

                    totalRead += chunkRead;
                }

                return thumbBytes;
            }
        }
        catch
        {
            // Suppress and return null
        }

        return null;
    }

    private static (int Offset, bool IsLittleEndian) FindTiffHeader(ReadOnlySpan<byte> span)
    {
        // Check for direct TIFF header (RAW files like CR2, NEF, ARW, DNG)
        if (span.Length >= 8)
        {
            if (span[0] == 0x49 && span[1] == 0x49 && span[2] == 0x2A && span[3] == 0x00)
            {
                return (0, true);
            }

            if (span[0] == 0x4D && span[1] == 0x4D && span[2] == 0x00 && span[3] == 0x2A)
            {
                return (0, false);
            }
        }

        // Check for JPEG APP1 Exif marker
        if (span.Length >= 14 && span[0] == 0xFF && span[1] == 0xD8)
        {
            int offset = 2;
            while (offset + 4 < span.Length)
            {
                if (span[offset] != 0xFF)
                {
                    break;
                }

                byte marker = span[offset + 1];
                int length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2));
                if (marker == 0xE1) // APP1
                {
                    int exifSigOffset = offset + 4;
                    if (exifSigOffset + 6 <= span.Length &&
                        span[exifSigOffset] == 'E' &&
                        span[exifSigOffset + 1] == 'x' &&
                        span[exifSigOffset + 2] == 'i' &&
                        span[exifSigOffset + 3] == 'f' &&
                        span[exifSigOffset + 4] == 0 &&
                        span[exifSigOffset + 5] == 0)
                    {
                        int tiffStart = exifSigOffset + 6;
                        if (tiffStart + 4 <= span.Length)
                        {
                            bool littleEndian = span[tiffStart] == 0x49 && span[tiffStart + 1] == 0x49;
                            return (tiffStart, littleEndian);
                        }
                    }
                }

                offset += 2 + length;
            }
        }

        return (-1, true);
    }

    private static ExifMetadataInfo ParseExifSpan(ReadOnlySpan<byte> span)
    {
        (int tiffOffset, bool isLittleEndian) = FindTiffHeader(span);
        if (tiffOffset < 0 || tiffOffset + 8 > span.Length)
        {
            return ExifMetadataInfo.Empty;
        }

        ReadOnlySpan<byte> tiffData = span[tiffOffset..];
        uint ifd0Offset = ReadUInt32(tiffData, 4, isLittleEndian);
        if (ifd0Offset == 0 || ifd0Offset + 2 > tiffData.Length)
        {
            return ExifMetadataInfo.Empty;
        }

        string? cameraMake = null;
        string? cameraModel = null;
        string? dateTime = null;
        string? lensModel = null;
        string? iso = null;
        string? exposureTime = null;
        string? fNumber = null;
        string? focalLength = null;
        int width = 0;
        int height = 0;
        uint exifIfdOffset = 0;

        // Parse IFD0
        int ifdOffset = (int)ifd0Offset;
        if (ifdOffset + 2 <= tiffData.Length)
        {
            ushort entryCount = ReadUInt16(tiffData, ifdOffset, isLittleEndian);
            ifdOffset += 2;

            for (int i = 0; i < entryCount && ifdOffset + 12 <= tiffData.Length; i++, ifdOffset += 12)
            {
                ushort tag = ReadUInt16(tiffData, ifdOffset, isLittleEndian);
                ushort type = ReadUInt16(tiffData, ifdOffset + 2, isLittleEndian);
                uint count = ReadUInt32(tiffData, ifdOffset + 4, isLittleEndian);

                switch (tag)
                {
                    case TAG_MAKE:
                        cameraMake = ReadString(tiffData, ifdOffset + 8, count, isLittleEndian);
                        break;
                    case TAG_MODEL:
                        cameraModel = ReadString(tiffData, ifdOffset + 8, count, isLittleEndian);
                        break;
                    case TAG_DATE_TIME:
                        dateTime = ReadString(tiffData, ifdOffset + 8, count, isLittleEndian);
                        break;
                    case TAG_IMAGE_WIDTH:
                        width = (int)ReadNumericValue(tiffData, ifdOffset + 8, type, isLittleEndian);
                        break;
                    case TAG_IMAGE_HEIGHT:
                        height = (int)ReadNumericValue(tiffData, ifdOffset + 8, type, isLittleEndian);
                        break;
                    case TAG_EXIF_IFD:
                        exifIfdOffset = ReadUInt32(tiffData, ifdOffset + 8, isLittleEndian);
                        break;
                }
            }
        }

        // Parse Exif SubIFD if present
        if (exifIfdOffset > 0 && exifIfdOffset + 2 <= tiffData.Length)
        {
            int subIfdOffset = (int)exifIfdOffset;
            ushort entryCount = ReadUInt16(tiffData, subIfdOffset, isLittleEndian);
            subIfdOffset += 2;

            for (int i = 0; i < entryCount && subIfdOffset + 12 <= tiffData.Length; i++, subIfdOffset += 12)
            {
                ushort tag = ReadUInt16(tiffData, subIfdOffset, isLittleEndian);
                ushort type = ReadUInt16(tiffData, subIfdOffset + 2, isLittleEndian);
                uint count = ReadUInt32(tiffData, subIfdOffset + 4, isLittleEndian);

                switch (tag)
                {
                    case TAG_DATE_TIME_ORIGINAL:
                        string? dtOriginal = ReadString(tiffData, subIfdOffset + 8, count, isLittleEndian);
                        if (!string.IsNullOrWhiteSpace(dtOriginal))
                        {
                            dateTime = dtOriginal;
                        }
                        break;
                    case TAG_ISO:
                        uint isoVal = ReadNumericValue(tiffData, subIfdOffset + 8, type, isLittleEndian);
                        if (isoVal > 0)
                        {
                            iso = $"ISO {isoVal}";
                        }
                        break;
                    case TAG_EXPOSURE_TIME:
                        exposureTime = ReadRational(tiffData, subIfdOffset + 8, isLittleEndian, isShutter: true);
                        break;
                    case TAG_F_NUMBER:
                        fNumber = ReadRational(tiffData, subIfdOffset + 8, isLittleEndian, isAperture: true);
                        break;
                    case TAG_FOCAL_LENGTH:
                        focalLength = ReadRational(tiffData, subIfdOffset + 8, isLittleEndian, isFocalLength: true);
                        break;
                    case TAG_PIXEL_X_DIMENSION:
                        int px = (int)ReadNumericValue(tiffData, subIfdOffset + 8, type, isLittleEndian);
                        if (px > 0) width = px;
                        break;
                    case TAG_PIXEL_Y_DIMENSION:
                        int py = (int)ReadNumericValue(tiffData, subIfdOffset + 8, type, isLittleEndian);
                        if (py > 0) height = py;
                        break;
                    case TAG_LENS_MODEL:
                        lensModel = ReadString(tiffData, subIfdOffset + 8, count, isLittleEndian);
                        break;
                }
            }
        }

        string? dimensions = (width > 0 && height > 0) ? $"{width} × {height}" : null;

        return new ExifMetadataInfo(
            CameraMake: CleanString(cameraMake),
            CameraModel: CleanString(cameraModel),
            LensModel: CleanString(lensModel),
            DateTimeOriginal: CleanString(dateTime),
            Dimensions: dimensions,
            Iso: iso,
            ExposureTime: exposureTime,
            FNumber: fNumber,
            FocalLength: focalLength,
            HasExif: true);
    }

    private static (long Offset, int Length) FindThumbnailOffsets(
        ReadOnlySpan<byte> span,
        int tiffStart,
        bool isLittleEndian)
    {
        if (tiffStart + 8 > span.Length)
        {
            return (0, 0);
        }

        ReadOnlySpan<byte> tiffData = span[tiffStart..];
        uint ifd0Offset = ReadUInt32(tiffData, 4, isLittleEndian);
        if (ifd0Offset == 0 || ifd0Offset + 2 > tiffData.Length)
        {
            return (0, 0);
        }

        int ifdOffset = (int)ifd0Offset;
        ushort entryCount = ReadUInt16(tiffData, ifdOffset, isLittleEndian);
        ifdOffset += 2;

        uint thumbOffset = 0;
        uint thumbLength = 0;

        for (int i = 0; i < entryCount && ifdOffset + 12 <= tiffData.Length; i++, ifdOffset += 12)
        {
            ushort tag = ReadUInt16(tiffData, ifdOffset, isLittleEndian);
            ushort type = ReadUInt16(tiffData, ifdOffset + 2, isLittleEndian);

            if (tag == TAG_JPEG_OFFSET)
            {
                thumbOffset = ReadNumericValue(tiffData, ifdOffset + 8, type, isLittleEndian);
            }
            else if (tag == TAG_JPEG_LENGTH)
            {
                thumbLength = ReadNumericValue(tiffData, ifdOffset + 8, type, isLittleEndian);
            }
        }

        if (thumbOffset == 0 || thumbLength == 0)
        {
            int nextIfdPtr = (int)ifd0Offset + 2 + (entryCount * 12);
            if (nextIfdPtr + 4 <= tiffData.Length)
            {
                uint ifd1Offset = ReadUInt32(tiffData, nextIfdPtr, isLittleEndian);
                if (ifd1Offset > 0 && ifd1Offset + 2 <= tiffData.Length)
                {
                    int ifd1Pos = (int)ifd1Offset;
                    ushort ifd1Count = ReadUInt16(tiffData, ifd1Pos, isLittleEndian);
                    ifd1Pos += 2;

                    for (int i = 0; i < ifd1Count && ifd1Pos + 12 <= tiffData.Length; i++, ifd1Pos += 12)
                    {
                        ushort tag = ReadUInt16(tiffData, ifd1Pos, isLittleEndian);
                        ushort type = ReadUInt16(tiffData, ifd1Pos + 2, isLittleEndian);

                        if (tag == TAG_JPEG_OFFSET)
                        {
                            thumbOffset = ReadNumericValue(tiffData, ifd1Pos + 8, type, isLittleEndian);
                        }
                        else if (tag == TAG_JPEG_LENGTH)
                        {
                            thumbLength = ReadNumericValue(tiffData, ifd1Pos + 8, type, isLittleEndian);
                        }
                    }
                }
            }
        }

        if (thumbOffset > 0 && thumbLength > 0)
        {
            return (tiffStart + thumbOffset, (int)thumbLength);
        }

        return (0, 0);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, int offset, bool isLittleEndian)
    {
        if (offset + 2 > span.Length) return 0;
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset, bool isLittleEndian)
    {
        if (offset + 4 > span.Length) return 0;
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
    }

    private static uint ReadNumericValue(ReadOnlySpan<byte> span, int offset, ushort type, bool isLittleEndian)
    {
        if (type == 3) // SHORT (16-bit)
        {
            return ReadUInt16(span, offset, isLittleEndian);
        }

        return ReadUInt32(span, offset, isLittleEndian);
    }

    private static string? ReadString(ReadOnlySpan<byte> span, int offset, uint count, bool isLittleEndian)
    {
        if (count == 0 || count > 512) return null;

        int valueOffset;
        if (count <= 4)
        {
            valueOffset = offset;
        }
        else
        {
            valueOffset = (int)ReadUInt32(span, offset, isLittleEndian);
        }

        if (valueOffset < 0 || valueOffset + count > span.Length) return null;

        ReadOnlySpan<byte> strSpan = span.Slice(valueOffset, (int)count);
        int nullIdx = strSpan.IndexOf((byte)0);
        if (nullIdx >= 0)
        {
            strSpan = strSpan.Slice(0, nullIdx);
        }

        return Encoding.UTF8.GetString(strSpan).Trim();
    }

    private static string? ReadRational(
        ReadOnlySpan<byte> span,
        int offset,
        bool isLittleEndian,
        bool isShutter = false,
        bool isAperture = false,
        bool isFocalLength = false)
    {
        uint valOffset = ReadUInt32(span, offset, isLittleEndian);
        if (valOffset + 8 > span.Length) return null;

        uint numerator = ReadUInt32(span, (int)valOffset, isLittleEndian);
        uint denominator = ReadUInt32(span, (int)valOffset + 4, isLittleEndian);

        if (denominator == 0) return null;

        if (isShutter)
        {
            if (numerator == 0) return "0s";
            if (numerator < denominator)
            {
                double fraction = Math.Round(denominator / (double)numerator);
                return $"1/{fraction:F0}s";
            }

            double seconds = numerator / (double)denominator;
            return $"{seconds:F1}s";
        }

        if (isAperture)
        {
            double fVal = numerator / (double)denominator;
            return $"f/{fVal:F1}";
        }

        if (isFocalLength)
        {
            double focalVal = numerator / (double)denominator;
            return $"{focalVal:F0}mm";
        }

        return $"{numerator}/{denominator}";
    }

    private static string? CleanString(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        return val.Trim().TrimEnd('\0');
    }
}
