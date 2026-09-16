using System;
using System.Collections.Generic;
using BatchPdfPublisher.Services;

// Dedicated coverage for the paper-size catalog. These rules back the GB/T
// 50001-2017 sheet library that ships with the product, and a wrong size here
// silently produces non-standard sheets or compressed plots, so the standard
// table itself is asserted explicitly rather than inferred.
internal static class PaperSizeCatalogTests
{
    // GB/T 50001-2017 表 3.1.3: short side (b) is never extended; only the long
    // side (l) is, and only by the listed fractions.
    private static readonly Dictionary<string, double[]> ExpectedLongSides = new Dictionary<string, double[]>
    {
        { "A0", new[] { 1486d, 1783d, 2080d, 2378d } },
        { "A1", new[] { 1051d, 1261d, 1471d, 1682d, 1892d, 2102d } },
        { "A2", new[] { 743d, 891d, 1041d, 1189d, 1338d, 1486d, 1635d, 1783d, 1932d, 2080d } },
        { "A3", new[] { 630d, 841d, 1051d, 1261d, 1471d, 1682d, 1892d } }
    };

    private static readonly Dictionary<string, double> ShortSides = new Dictionary<string, double>
    {
        { "A0", 841d }, { "A1", 594d }, { "A2", 420d }, { "A3", 297d }, { "A4", 210d }
    };

    public static void RunAll()
    {
        StandardSheetsUseExactStandardLengths();
        LandscapeSwapsShortAndLongSide();
        A4IsPortraitByDefault();
        ExtendedFractionsAreAcceptedOnlyWhenStandard();
        ParsesFractionAndDecimalExtensions();
        RejectsNonStandardExtensions();
        RoundTripsEverySupportedSize();
        IdentifiesRotatedBlocksByLongAndShortSide();
        DescribesMachineReadableSizes();
    }

    // Every supported extension of every basic sheet must resolve to exactly the
    // tabulated long side. ExtendedLongSides is consulted before the arithmetic
    // fallback, which is what keeps A1+1/2l at 1261 mm instead of 1262 mm.
    private static void StandardSheetsUseExactStandardLengths()
    {
        foreach (var pair in ExpectedLongSides)
        {
            var paper = pair.Key;
            var expected = pair.Value;
            var supported = PaperSizeCatalog.GetSupportedExtensions(paper);
            Assert(supported.Length == expected.Length + 1,
                paper + " should expose " + (expected.Length + 1) + " extensions but exposes " + supported.Length);

            for (var index = 0; index < expected.Length; index++)
            {
                var extension = supported[index + 1];
                var size = PaperSizeCatalog.GetSize(paper, extension, "横向");
                Assert(size[0] == expected[index],
                    paper + "+" + extension + " long side should be " + expected[index] + " but was " + size[0]);
                Assert(size[1] == ShortSides[paper],
                    paper + "+" + extension + " short side should stay " + ShortSides[paper] + " but was " + size[1]);
            }
        }
        Console.WriteLine("PASS PaperCatalogUsesExactStandardLengths");
    }

    private static void LandscapeSwapsShortAndLongSide()
    {
        var landscape = PaperSizeCatalog.GetSize("A2", "1/2", "横向");
        var portrait = PaperSizeCatalog.GetSize("A2", "1/2", "纵向");
        Assert(landscape[0] == 891d && landscape[1] == 420d, "A2+1/2 horizontal should be 891x420");
        Assert(portrait[0] == 420d && portrait[1] == 891d, "A2+1/2 vertical should be 420x891");
        Console.WriteLine("PASS PaperCatalogLandscapeSwapsSides");
    }

    private static void A4IsPortraitByDefault()
    {
        Assert(PaperSizeCatalog.DefaultOrientation("A4") == "纵向", "A4 must default to portrait");
        Assert(PaperSizeCatalog.DefaultOrientation("A3") == "横向", "A3 must default to landscape");
        var size = PaperSizeCatalog.GetSize("A4", string.Empty, PaperSizeCatalog.DefaultOrientation("A4"));
        Assert(size[0] == 210d && size[1] == 297d, "A4 portrait should be 210x297");
        Console.WriteLine("PASS PaperCatalogA4DefaultsToPortrait");
    }

    // ParseExtension is deliberately permissive about notation, but acceptance of
    // a fraction is a separate decision from being able to parse it.
    private static void ExtendedFractionsAreAcceptedOnlyWhenStandard()
    {
        Assert(PaperSizeCatalog.IsStandardExtension("A1", "1/2"), "A1+1/2l is a standard extension");
        Assert(PaperSizeCatalog.IsStandardExtension("A3", "7/2"), "A3+7/2l is a standard extension");
        Assert(PaperSizeCatalog.ParseExtension("A1+1/2l") == 0.5d, "A1+1/2l should parse to 0.5");
        Assert(PaperSizeCatalog.NormalizeExtension("A2L") == string.Empty, "A2L normalizes to the basic sheet");
        Console.WriteLine("PASS PaperCatalogAcceptsOnlyStandardExtensions");
    }

    private static void ParsesFractionAndDecimalExtensions()
    {
        Assert(PaperSizeCatalog.ParseExtension(null) == 0d, "null extension is the basic sheet");
        Assert(PaperSizeCatalog.ParseExtension(string.Empty) == 0d, "empty extension is the basic sheet");
        Assert(PaperSizeCatalog.ParseExtension("1/4") == 0.25d, "1/4 should parse to 0.25");
        Assert(PaperSizeCatalog.ParseExtension("3/2") == 1.5d, "3/2 should parse to 1.5");
        Assert(PaperSizeCatalog.ParseExtension("1.5") == 1.5d, "1.5 should parse to 1.5");
        Assert(PaperSizeCatalog.ParseExtension("1/0") == 0d, "a zero denominator must not divide");
        Assert(PaperSizeCatalog.FormatExtension(0.25d) == "1/4", "0.25 formats as 1/4");
        Assert(PaperSizeCatalog.FormatExtension(1.5d) == "3/2", "1.5 formats as 3/2");
        Assert(PaperSizeCatalog.FormatExtension(2.5d) == "5/2", "2.5 formats as 5/2");
        Console.WriteLine("PASS PaperCatalogParsesExtensions");
    }

    // A3+1/2l is standard at 630 mm. A0+1/4l is standard but A0+1/8l is not, and
    // short-side extension is never standard because A4 exposes no extension.
    private static void RejectsNonStandardExtensions()
    {
        Assert(PaperSizeCatalog.IsStandardExtension("A3", "1/2"), "A3+1/2l is standard");
        Assert(!PaperSizeCatalog.IsStandardExtension("A0", "1/8"), "A0+1/8l is not in the table");
        Assert(!PaperSizeCatalog.IsStandardExtension("A1", "2"), "A1+2l is not in the table");
        Assert(!PaperSizeCatalog.IsStandardExtension("A4", "1/4"), "A4 must not be extended");
        Assert(PaperSizeCatalog.IsStandardExtension("A4", "L"), "the basic A4 sheet is always standard");
        Assert(PaperSizeCatalog.GetSupportedExtensions("A4").Length == 1, "A4 exposes only the basic sheet");

        double[] unused;
        Assert(!PaperSizeCatalog.TryGetStandardSize("A1", "2", "横向", out unused),
            "a non-standard extension must be refused instead of silently sized");
        Assert(PaperSizeCatalog.TryGetStandardSize("A1", "1", "横向", out unused),
            "a standard extension must be accepted");
        Console.WriteLine("PASS PaperCatalogRejectsNonStandardExtensions");
    }

    // Identify is the inverse of GetSize for every catalogued sheet, in both the
    // catalog direction and a physically rotated source block.
    private static void RoundTripsEverySupportedSize()
    {
        var papers = new[] { "A0", "A1", "A2", "A3", "A4" };
        foreach (var paper in papers)
            foreach (var extension in PaperSizeCatalog.GetSupportedExtensions(paper))
            {
                var orientation = PaperSizeCatalog.DefaultOrientation(paper);
                var size = PaperSizeCatalog.GetSize(paper, extension, orientation);
                string foundPaper, foundExtension, foundOrientation;
                Assert(PaperSizeCatalog.TryIdentify(size[0], size[1], out foundPaper, out foundExtension, out foundOrientation),
                    "could not identify " + paper + "+" + extension + " from " + size[0] + "x" + size[1]);
                Assert(foundPaper == paper && foundExtension == extension,
                    "identified " + paper + "+" + extension + " as " + foundPaper + "+" + foundExtension);
            }
        Console.WriteLine("PASS PaperCatalogRoundTripsEverySupportedSize");
    }

    private static void IdentifiesRotatedBlocksByLongAndShortSide()
    {
        // A horizontal A1+1/2l sheet is 1261x594; rotated in CAD it measures 594x1261.
        string paper, extension, orientation;
        Assert(PaperSizeCatalog.TryIdentify(1261d, 594d, out paper, out extension, out orientation),
            "1261x594 should identify the catalog direction of A1+1/2l");
        Assert(paper == "A1" && extension == "1/2", "expected A1+1/2l but got " + paper + "+" + extension);
        Assert(PaperSizeCatalog.TryIdentify(594d, 1261d, out paper, out extension, out orientation),
            "a rotated A1+1/2l block should still be identified");
        Assert(paper == "A1" && extension == "1/2", "rotated block should map back to A1+1/2l");
        Assert(!PaperSizeCatalog.TryIdentify(1234d, 567d, out paper, out extension, out orientation),
            "an arbitrary size must not be identified as a standard sheet");
        Console.WriteLine("PASS PaperCatalogIdentifiesRotatedBlocks");
    }

    private static void DescribesMachineReadableSizes()
    {
        Assert(PaperSizeCatalog.Describe("A2", "1/2", "横向") == "891 × 420 mm",
            "description should be machine readable, was " + PaperSizeCatalog.Describe("A2", "1/2", "横向"));
        Assert(PaperSizeCatalog.DescribeStandardExtensions("A4") == "无（该幅面短边不应加长）",
            "A4 has no standard extension to list");
        Assert(PaperSizeCatalog.DescribeStandardExtensions("A3").Contains("1/2"),
            "A3 extension list should mention 1/2");
        Console.WriteLine("PASS PaperCatalogDescribesSizes");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
