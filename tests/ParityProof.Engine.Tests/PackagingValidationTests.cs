using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class PackagingValidationTests
{
    [Fact]
    public void WindowsWixPackagingFile_IsValidXmlAndContainsRequiredComponents()
    {
        string? repoRoot = FindRepositoryRoot();
        Assert.NotNull(repoRoot);

        string wixFilePath = Path.Combine(repoRoot, "packaging", "windows", "ParityProof.wxs");
        Assert.True(File.Exists(wixFilePath), $"WiX source file not found at: {wixFilePath}");

        XDocument doc = XDocument.Load(wixFilePath);
        Assert.NotNull(doc.Root);

        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        XElement? package = doc.Root.Element(ns + "Package");
        Assert.NotNull(package);

        XElement? feature = package.Element(ns + "Feature");
        Assert.NotNull(feature);

        List<XElement> components = new(feature.Elements(ns + "Component"));
        Assert.True(components.Count >= 2, "Expected at least 2 Component declarations in MainFeature.");
    }

    private static string? FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "ParityProof.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }
}
