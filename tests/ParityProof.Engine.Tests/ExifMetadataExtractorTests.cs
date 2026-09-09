using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using ParityProof.Core.Models;
using ParityProof.Engine.IO;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class ExifMetadataExtractorTests : IDisposable
{
    private readonly string _tempDir;

    public ExifMetadataExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ParityProof_ExifTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ExtractMetadata_NonExistentFile_ReturnsEmpty()
    {
        string fakePath = Path.Combine(_tempDir, "does_not_exist.jpg");
        ExifMetadataInfo metadata = ExifMetadataExtractor.ExtractMetadata(fakePath);

        Assert.NotNull(metadata);
        Assert.False(metadata.HasExif);
        Assert.Null(metadata.CameraModel);
    }

    [Fact]
    public void ExtractMetadata_EmptyFile_ReturnsEmpty()
    {
        string emptyPath = Path.Combine(_tempDir, "empty.jpg");
        File.WriteAllBytes(emptyPath, Array.Empty<byte>());

        ExifMetadataInfo metadata = ExifMetadataExtractor.ExtractMetadata(emptyPath);
        Assert.False(metadata.HasExif);
    }

    [Fact]
    public void ExtractMetadata_TiffRawHeader_ParsesCameraAndExposureTags()
    {
        string rawFilePath = Path.Combine(_tempDir, "sample.cr2");
        byte[] tiffBytes = CreateSyntheticTiffRawHeader(
            make: "Canon",
            model: "Canon EOS R5",
            lens: "RF24-70mm F2.8 L IS USM",
            iso: 400,
            shutterNumerator: 1,
            shutterDenominator: 500,
            apertureNumerator: 28,
            apertureDenominator: 10,
            focalNumerator: 70,
            focalDenominator: 1,
            width: 8192,
            height: 5464);

        File.WriteAllBytes(rawFilePath, tiffBytes);

        ExifMetadataInfo metadata = ExifMetadataExtractor.ExtractMetadata(rawFilePath);

        Assert.True(metadata.HasExif);
        Assert.Equal("Canon", metadata.CameraMake);
        Assert.Equal("Canon EOS R5", metadata.CameraModel);
        Assert.Equal("RF24-70mm F2.8 L IS USM", metadata.LensModel);
        Assert.Equal("ISO 400", metadata.Iso);
        Assert.Equal("1/500s", metadata.ExposureTime);
        Assert.Equal("f/2.8", metadata.FNumber);
        Assert.Equal("70mm", metadata.FocalLength);
        Assert.Equal("8192 × 5464", metadata.Dimensions);
        Assert.Contains("Canon EOS R5", metadata.FormattedCameraSummary);
        Assert.Contains("1/500s", metadata.FormattedExposureSummary);
    }

    [Fact]
    public void ExtractThumbnailBytes_JpegFile_ReturnsRawBytes()
    {
        string jpgPath = Path.Combine(_tempDir, "photo.jpg");
        byte[] payload = new byte[1024];
        payload[0] = 0xFF;
        payload[1] = 0xD8;
        payload[2] = 0xFF;
        payload[3] = 0xD9;
        File.WriteAllBytes(jpgPath, payload);

        byte[]? thumb = ExifMetadataExtractor.ExtractThumbnailBytes(jpgPath);

        Assert.NotNull(thumb);
        Assert.Equal(payload.Length, thumb.Length);
    }

    private static byte[] CreateSyntheticTiffRawHeader(
        string make,
        string model,
        string lens,
        ushort iso,
        uint shutterNumerator,
        uint shutterDenominator,
        uint apertureNumerator,
        uint apertureDenominator,
        uint focalNumerator,
        uint focalDenominator,
        uint width,
        uint height)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms);

        // 1. TIFF Header (Little-Endian)
        bw.Write((byte)0x49); // 'I'
        bw.Write((byte)0x49); // 'I'
        bw.Write((ushort)42); // Magic
        bw.Write((uint)8);    // Offset to IFD0

        // 2. IFD0
        // Entries: Make (0x010F), Model (0x0110), Width (0x0100), Height (0x0101), ExifIFD (0x8769)
        ushort ifd0Count = 5;
        bw.Write(ifd0Count);

        // Layout offsets
        uint makeOffset = 250;
        uint modelOffset = 300;
        uint exifIfdOffset = 100;

        // Entry 1: TAG_IMAGE_WIDTH (0x0100), LONG (4), Count 1
        bw.Write((ushort)0x0100);
        bw.Write((ushort)4);
        bw.Write((uint)1);
        bw.Write(width);

        // Entry 2: TAG_IMAGE_HEIGHT (0x0101), LONG (4), Count 1
        bw.Write((ushort)0x0101);
        bw.Write((ushort)4);
        bw.Write((uint)1);
        bw.Write(height);

        // Entry 3: TAG_MAKE (0x010F), ASCII (2)
        byte[] makeBytes = Encoding.UTF8.GetBytes(make + "\0");
        bw.Write((ushort)0x010F);
        bw.Write((ushort)2);
        bw.Write((uint)makeBytes.Length);
        bw.Write(makeOffset);

        // Entry 4: TAG_MODEL (0x0110), ASCII (2)
        byte[] modelBytes = Encoding.UTF8.GetBytes(model + "\0");
        bw.Write((ushort)0x0110);
        bw.Write((ushort)2);
        bw.Write((uint)modelBytes.Length);
        bw.Write(modelOffset);

        // Entry 5: TAG_EXIF_IFD (0x8769), LONG (4)
        bw.Write((ushort)0x8769);
        bw.Write((ushort)4);
        bw.Write((uint)1);
        bw.Write(exifIfdOffset);

        // Next IFD offset = 0
        bw.Write((uint)0);

        // Pad to exifIfdOffset (100)
        while (ms.Position < exifIfdOffset)
        {
            bw.Write((byte)0);
        }

        // 3. Exif SubIFD at offset 100
        // Entries: ExposureTime (0x829A), FNumber (0x829D), ISO (0x8827), FocalLength (0x920A), LensModel (0xA434)
        ushort exifCount = 5;
        bw.Write(exifCount);

        uint shutterValOffset = 350;
        uint apertureValOffset = 360;
        uint focalValOffset = 370;
        uint lensOffset = 400;

        // ExposureTime (0x829A), RATIONAL (5), count 1
        bw.Write((ushort)0x829A);
        bw.Write((ushort)5);
        bw.Write((uint)1);
        bw.Write(shutterValOffset);

        // FNumber (0x829D), RATIONAL (5), count 1
        bw.Write((ushort)0x829D);
        bw.Write((ushort)5);
        bw.Write((uint)1);
        bw.Write(apertureValOffset);

        // ISO (0x8827), SHORT (3), count 1
        bw.Write((ushort)0x8827);
        bw.Write((ushort)3);
        bw.Write((uint)1);
        bw.Write((ushort)iso);
        bw.Write((ushort)0); // padding

        // FocalLength (0x920A), RATIONAL (5), count 1
        bw.Write((ushort)0x920A);
        bw.Write((ushort)5);
        bw.Write((uint)1);
        bw.Write(focalValOffset);

        // LensModel (0xA434), ASCII (2)
        byte[] lensBytes = Encoding.UTF8.GetBytes(lens + "\0");
        bw.Write((ushort)0xA434);
        bw.Write((ushort)2);
        bw.Write((uint)lensBytes.Length);
        bw.Write(lensOffset);

        // Pad and write values
        WriteAtOffset(ms, bw, makeOffset, makeBytes);
        WriteAtOffset(ms, bw, modelOffset, modelBytes);

        WriteRationalAtOffset(ms, bw, shutterValOffset, shutterNumerator, shutterDenominator);
        WriteRationalAtOffset(ms, bw, apertureValOffset, apertureNumerator, apertureDenominator);
        WriteRationalAtOffset(ms, bw, focalValOffset, focalNumerator, focalDenominator);
        WriteAtOffset(ms, bw, lensOffset, lensBytes);

        return ms.ToArray();
    }

    private static void WriteAtOffset(MemoryStream ms, BinaryWriter bw, uint offset, byte[] data)
    {
        while (ms.Position < offset)
        {
            bw.Write((byte)0);
        }
        ms.Position = offset;
        bw.Write(data);
    }

    private static void WriteRationalAtOffset(MemoryStream ms, BinaryWriter bw, uint offset, uint num, uint den)
    {
        while (ms.Position < offset)
        {
            bw.Write((byte)0);
        }
        ms.Position = offset;
        bw.Write(num);
        bw.Write(den);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Suppress cleanup
        }
    }
}
