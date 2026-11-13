using MetroCIM.Models;
using MetroCIM.Services;
using Xunit;

namespace MetroCIM.Tests;

public sealed class IfcColorPatcherTests
{
    [Fact]
    public void SplitArgsKeepsNestedListsTogether()
    {
        IReadOnlyList<string> args = IfcColorPatcher.SplitArgs("'guid',#2,'Pipe',$,$,#11,#12,'1'");
        Assert.Equal(8, args.Count);
        Assert.Equal("'guid'", args[0]);
        Assert.Equal("#12", args[6]);
    }

    [Fact]
    public void PatchTextAddsAUniqueSurfaceStyleToThePipeBody()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#10=IFCFLOWSEGMENT('0k2Hq$n6f8VhP8nZQlY$xK',#1,'Pipe',$,$,#11,#12,'1');\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','SweptSolid',(#14));\n" +
            "#14=IFCEXTRUDEDAREASOLID(#15,#16,#17,1.0);\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        var colors = new Dictionary<string, ResolvedAppearance>
        {
            ["0k2Hq$n6f8VhP8nZQlY$xK"] = ResolvedAppearance.FromRgb(0, 176, 240)
        };

        string patched = IfcColorPatcher.PatchText(ifc, colors);

        Assert.Contains("IFCCOLOURRGB($,0.,0.690196,0.941176)", patched);
        Assert.Contains("IFCSURFACESTYLE('MetroCIM',.BOTH.", patched);
        Assert.Contains("IFCSTYLEDITEM(#14,", patched);
        Assert.Contains("ENDSEC;", patched);
    }

    [Fact]
    public void PatchTextStylesMappedFittingGeometryAndRebindsMaterial()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#2=IFCOWNERHISTORY(#3,#4,$,.NOCHANGE.,$,$,$,0);\n" +
            "#10=IFCFLOWFITTING('0k2Hq$n6f8VhP8nZQlY$xK',#2,'Elbow',$,$,#11,#12,'55',.NOTDEFINED.);\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','MappedRepresentation',(#14));\n" +
            "#14=IFCMAPPEDITEM(#30,#31);\n" +
            "#40=IFCRELASSOCIATESMATERIAL('2aaaaaaaaaaaaaaaaaaaaa',#2,$,$,(#10),#41);\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        var colors = new Dictionary<string, ResolvedAppearance>
        {
            ["0k2Hq$n6f8VhP8nZQlY$xK"] = ResolvedAppearance.FromRgb(0, 176, 240)
        };

        string patched = IfcColorPatcher.PatchText(ifc, colors);

        Assert.Contains("IFCSTYLEDITEM(#14,", patched);
        Assert.Contains("IFCMATERIAL('MetroCIM 000-176-240-255')", patched);
        Assert.Contains("IFCRELASSOCIATESMATERIAL('2aaaaaaaaaaaaaaaaaaaaa',#2,$,$,(),#41);", patched);
        Assert.Contains("IFCRELASSOCIATESMATERIAL(", patched);
        Assert.Contains(",(#10),#", patched);
    }

    [Fact]
    public void PatchTextReplacesAnExistingStyledItem()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#10=IFCFLOWSEGMENT('0k2Hq$n6f8VhP8nZQlY$xK',#1,'Pipe',$,$,#11,#12,'1');\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','SweptSolid',(#14));\n" +
            "#14=IFCEXTRUDEDAREASOLID(#15,#16,#17,1.0);\n" +
            "#20=IFCSTYLEDITEM(#14,(#21),$);\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        var colors = new Dictionary<string, ResolvedAppearance>
        {
            ["0k2Hq$n6f8VhP8nZQlY$xK"] = ResolvedAppearance.FromRgb(200, 40, 90)
        };

        string patched = IfcColorPatcher.PatchText(ifc, colors);

        Assert.Contains("IFCCOLOURRGB($,0.784314,0.156863,0.352941)", patched);
        Assert.DoesNotContain("#20=IFCSTYLEDITEM(#14,(#21),$);", patched);
        Assert.Contains("#20=IFCSTYLEDITEM(#14,", patched);
    }

    [Fact]
    public void PatchTextDoesNotTreatANumericTagAsAnElementId()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#10=IFCFLOWFITTING('0k2Hq$n6f8VhP8nZQlY$xK',#1,'Elbow',$,$,#11,#12,'55');\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','SweptSolid',(#14));\n" +
            "#14=IFCEXTRUDEDAREASOLID(#15,#16,#17,1.0);\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        var colors = new Dictionary<string, ResolvedAppearance>
        {
            ["55"] = ResolvedAppearance.FromRgb(255, 0, 0)
        };

        string patched = IfcColorPatcher.PatchText(ifc, colors);

        Assert.DoesNotContain("IFCCOLOURRGB", patched);
        Assert.DoesNotContain("IFCSTYLEDITEM", patched);
    }

    [Fact]
    public void PatchTextClonesSharedMappedGeometryPerColor()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#2=IFCOWNERHISTORY(#3,#4,$,.NOCHANGE.,$,$,$,0);\n" +
            "#10=IFCFLOWFITTING('0k2Hq$n6f8VhP8nZQlY$xK',#2,'Elbow',$,$,#11,#12,'A');\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','MappedRepresentation',(#14));\n" +
            "#14=IFCMAPPEDITEM(#30,#31);\n" +
            "#20=IFCFLOWFITTING('1k2Hq$n6f8VhP8nZQlY$xK',#2,'Elbow',$,$,#21,#22,'B');\n" +
            "#22=IFCPRODUCTDEFINITIONSHAPE($,$,(#23));\n" +
            "#23=IFCSHAPEREPRESENTATION(#1,'Body','MappedRepresentation',(#24));\n" +
            "#24=IFCMAPPEDITEM(#30,#31);\n" +
            "#30=IFCREPRESENTATIONMAP(#32,#40);\n" +
            "#32=IFCAXIS2PLACEMENT3D(#33,$,$);\n" +
            "#33=IFCCARTESIANPOINT((0.,0.,0.));\n" +
            "#40=IFCSHAPEREPRESENTATION(#1,'Body','Tessellation',(#50));\n" +
            "#50=IFCTRIANGULATEDFACESET($,$,$,((0.,0.,0.)),$,((1,2,3)),$);\n" +
            "#60=IFCSTYLEDITEM(#50,(#61),$);\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        var colors = new Dictionary<string, ResolvedAppearance>
        {
            ["0k2Hq$n6f8VhP8nZQlY$xK"] = ResolvedAppearance.FromRgb(0, 176, 240),
            ["1k2Hq$n6f8VhP8nZQlY$xK"] = ResolvedAppearance.FromRgb(200, 40, 90)
        };

        string patched = IfcColorPatcher.PatchText(ifc, colors);

        Assert.Contains("IFCCOLOURRGB($,0.,0.690196,0.941176)", patched);
        Assert.Contains("IFCCOLOURRGB($,0.784314,0.156863,0.352941)", patched);
        Assert.DoesNotContain("#14=IFCMAPPEDITEM(#30,", patched);
        Assert.DoesNotContain("#24=IFCMAPPEDITEM(#30,", patched);
        Assert.Contains("#60=IFCSTYLEDITEM(#50,(#61),$);", patched);
        Assert.Contains("IFCTRIANGULATEDFACESET($,$,$,((0.,0.,0.)),$,((1,2,3)),$);", patched);
    }

    [Fact]
    public void PatchTextStylesSharedMappedGeometryInPlaceWhenEveryInstanceAgrees()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#10=IFCFLOWFITTING('0k2Hq$n6f8VhP8nZQlY$xK',#1,'Elbow',$,$,#11,#12,'A');\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','MappedRepresentation',(#14));\n" +
            "#14=IFCMAPPEDITEM(#30,#31);\n" +
            "#20=IFCFLOWFITTING('1k2Hq$n6f8VhP8nZQlY$xK',#1,'Elbow',$,$,#21,#22,'B');\n" +
            "#22=IFCPRODUCTDEFINITIONSHAPE($,$,(#23));\n" +
            "#23=IFCSHAPEREPRESENTATION(#1,'Body','MappedRepresentation',(#24));\n" +
            "#24=IFCMAPPEDITEM(#30,#31);\n" +
            "#30=IFCREPRESENTATIONMAP(#32,#40);\n" +
            "#40=IFCSHAPEREPRESENTATION(#1,'Body','Tessellation',(#50));\n" +
            "#50=IFCTRIANGULATEDFACESET($,$,$,((0.,0.,0.)),$,((1,2,3)),$);\n" +
            "#60=IFCSTYLEDITEM(#50,(#61),$);\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        var colors = new Dictionary<string, ResolvedAppearance>
        {
            ["0k2Hq$n6f8VhP8nZQlY$xK"] = ResolvedAppearance.FromRgb(0, 176, 240),
            ["1k2Hq$n6f8VhP8nZQlY$xK"] = ResolvedAppearance.FromRgb(0, 176, 240)
        };

        string patched = IfcColorPatcher.PatchText(ifc, colors);

        Assert.Contains("#60=IFCSTYLEDITEM(#50,", patched);
        Assert.DoesNotContain("#60=IFCSTYLEDITEM(#50,(#61),$);", patched);
        Assert.Contains("#14=IFCMAPPEDITEM(#30,#31);", patched);
        Assert.Contains("#24=IFCMAPPEDITEM(#30,#31);", patched);
    }

    [Fact]
    public void PatchTextInheritsColorOntoNestedAccessoryParts()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#10=IFCFLOWTERMINAL('0k2Hq$n6f8VhP8nZQlY$xK',#1,'Valve',$,$,#11,#12,'V');\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','SweptSolid',(#14));\n" +
            "#14=IFCEXTRUDEDAREASOLID(#15,#16,#17,1.0);\n" +
            "#20=IFCBUILDINGELEMENTPROXY('2k2Hq$n6f8VhP8nZQlY$xK',#1,'Wheel',$,$,#21,#22,'W');\n" +
            "#22=IFCPRODUCTDEFINITIONSHAPE($,$,(#23));\n" +
            "#23=IFCSHAPEREPRESENTATION(#1,'Body','SweptSolid',(#24));\n" +
            "#24=IFCEXTRUDEDAREASOLID(#25,#26,#27,1.0);\n" +
            "#30=IFCRELNESTS('3k2Hq$n6f8VhP8nZQlY$xK',#1,$,$,#10,(#20));\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        var colors = new Dictionary<string, ResolvedAppearance>
        {
            ["0k2Hq$n6f8VhP8nZQlY$xK"] = ResolvedAppearance.FromRgb(0, 176, 240)
        };

        string patched = IfcColorPatcher.PatchText(ifc, colors);

        Assert.Contains("IFCSTYLEDITEM(#14,", patched);
        Assert.Contains("IFCSTYLEDITEM(#24,", patched);
        Assert.Contains(",(#10,#20),#", patched);
    }
}

public sealed class IfcDeviceConeStripperTests
{
    [Fact]
    public void StripDropsThePyramidOnAnOutletAndKeepsTheBody()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#10=IFCOUTLET('0k2Hq$n6f8VhP8nZQlY$xK',#2,'Receptacle',$,$,#11,#12,'1');\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','Tessellation',(#50));\n" +
            "#50=IFCTRIANGULATEDFACESET(#51,$,.T.,((1,2,3),(1,2,4),(1,3,4),(2,3,4),(5,6,7)),$);\n" +
            "#51=IFCCARTESIANPOINTLIST3D(((0.,0.,0.),(1.,0.,0.),(0.5,1.,0.),(0.5,0.4,1.),(2.,0.,0.),(3.,0.,0.),(2.5,1.,0.)));\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        string stripped = IfcDeviceConeStripper.Strip(ifc);

        Assert.Contains("((5,6,7))", stripped);
        Assert.DoesNotContain("(1,2,3)", stripped);
        Assert.DoesNotContain("(2,3,4)", stripped);
    }

    [Fact]
    public void StripLeavesAPyramidOnAPipeFitting()
    {
        const string ifc =
            "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
            "#10=IFCFLOWFITTING('0k2Hq$n6f8VhP8nZQlY$xK',#2,'Elbow',$,$,#11,#12,'1');\n" +
            "#12=IFCPRODUCTDEFINITIONSHAPE($,$,(#13));\n" +
            "#13=IFCSHAPEREPRESENTATION(#1,'Body','Tessellation',(#50));\n" +
            "#50=IFCTRIANGULATEDFACESET(#51,$,.T.,((1,2,3),(1,2,4),(1,3,4),(2,3,4),(5,6,7)),$);\n" +
            "#51=IFCCARTESIANPOINTLIST3D(((0.,0.,0.),(1.,0.,0.),(0.5,1.,0.),(0.5,0.4,1.),(2.,0.,0.),(3.,0.,0.),(2.5,1.,0.)));\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n";

        string stripped = IfcDeviceConeStripper.Strip(ifc);

        Assert.Contains("(1,2,3)", stripped);
        Assert.Contains("(5,6,7)", stripped);
    }
}
