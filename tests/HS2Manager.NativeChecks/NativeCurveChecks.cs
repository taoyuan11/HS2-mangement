using System.Text.Json;
using Sim=HS2Manager.Core.Simulation;

internal static class NativeCurveChecks
{
    internal static void Run(string input,string output)
    {
        using var data=JsonDocument.Parse(File.ReadAllText(input));
        File.WriteAllText(output,JsonSerializer.Serialize(new{Curves=Evaluate(data.RootElement)}));
    }
    internal static void VerifyReference()
    {
        using var stream=typeof(NativeCurveChecks).Assembly.GetManifestResourceStream("native-curve-reference.json")??throw new InvalidDataException("The independent curve reference is missing.");
        using var fixture=JsonDocument.Parse(stream);
        using var actual=JsonSerializer.SerializeToDocument(new{Curves=Evaluate(fixture.RootElement.GetProperty("Input"))});
        var expected=fixture.RootElement.GetProperty("Expected").GetProperty("Curves");var curves=actual.RootElement.GetProperty("Curves");
        if(expected.GetArrayLength()!=curves.GetArrayLength())throw new InvalidDataException("Curve reference count changed.");
        for(int row=0;row<expected.GetArrayLength();row++)
        {
            var values=expected[row].GetProperty("Values");var result=curves[row].GetProperty("Values");
            if(values.GetArrayLength()!=result.GetArrayLength())throw new InvalidDataException("Curve sample count changed.");
            for(int i=0;i<values.GetArrayLength();i++)
                if(values[i].GetSingle()!=result[i].GetSingle())throw new Exception($"Curve reference differs: {row}/{i}.");
        }
    }
    private static object[] Evaluate(JsonElement input)=>input.GetProperty("Curves").EnumerateArray().Select(row=>
    {
        var curve=new Sim.AnimationCurve(row.GetProperty("Keys").EnumerateArray().Select(key=>new Sim.Keyframe(
            key.GetProperty("Time").GetSingle(),key.GetProperty("Value").GetSingle(),key.GetProperty("In").GetSingle(),key.GetProperty("Out").GetSingle(),
            key.GetProperty("InWeight").GetSingle(),key.GetProperty("OutWeight").GetSingle()){weightedMode=(Sim.WeightedMode)key.GetProperty("Weighted").GetInt32()}).ToArray())
            {preWrapMode=Wrap(row.GetProperty("Pre").GetInt32()),postWrapMode=Wrap(row.GetProperty("Post").GetInt32())};
        return (object)new{Id=row.GetProperty("Id").GetString(),Values=row.GetProperty("Times").EnumerateArray().Select(t=>curve.Evaluate(t.GetSingle())).ToArray()};
    }).ToArray();
    private static Sim.WrapMode Wrap(int value)=>value switch{2=>Sim.WrapMode.Loop,4=>Sim.WrapMode.PingPong,_=>Sim.WrapMode.ClampForever};
}
