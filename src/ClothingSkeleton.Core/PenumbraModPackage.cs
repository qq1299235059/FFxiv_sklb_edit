using System.IO.Compression;
using System.Text.Json;

namespace ClothingSkeleton.Core;

public sealed record MidlanderFemaleEst(ushort SetId,ushort SkeletonId,ushort? SourceSkeletonId=null)
{
    public string GamePath => $"chara/human/c0201/skeleton/top/t{SkeletonId:D4}/skl_c0201t{SkeletonId:D4}.sklb";
    public string? SourceGamePath => SourceSkeletonId is{} source?$"chara/human/c0201/skeleton/top/t{source:D4}/skl_c0201t{source:D4}.sklb":null;
}

public static class PenumbraModPackage
{
    public static string Export(string gamePath, string candidatePath, string destinationDirectory,MidlanderFemaleEst? est=null)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Contains('\\') || gamePath.Contains(':') ||
            gamePath.Split('/').Any(p => p is "" or "." or "..") ||
            !gamePath.StartsWith("chara/",StringComparison.Ordinal) || !gamePath.EndsWith(".sklb",StringComparison.Ordinal))
            throw new InvalidDataException("Expected an explicit normalized skeleton game path.");
        if(est is not null && (est.SetId==0 || est.SkeletonId is 0 or >9999 || est.SourceSkeletonId is 0 or >9999 || gamePath!=est.GamePath))
            throw new InvalidDataException("Midlander female EST assignment and skeleton path do not match.");
        _=SklbContainer.Parse(File.ReadAllBytes(candidatePath));
        if(Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory+".pmp")) throw new IOException("Choose a new package output directory.");
        Directory.CreateDirectory(Path.Combine(destinationDirectory,"files"));
        File.Copy(candidatePath,Path.Combine(destinationDirectory,"files","skeleton.sklb"));
        var options=new JsonSerializerOptions { WriteIndented=true };
        File.WriteAllText(Path.Combine(destinationDirectory,"meta.json"),JsonSerializer.Serialize(new
        {
            FileVersion=3, Name=est is null?"Clothing Skeleton - appended bones test":$"CSW Midlander Female Odin EST {est.SkeletonId}", Author="ClothingSkeleton contributors",
            Description=est is null?"Skeleton-only test. Clothing mesh weights and dynamic behavior are not included.":$"Original Midlander female clothing skeleton with appended test bone. Body EST e{est.SetId:D4} -> t{est.SkeletonId:D4}. No clothing vertex weights or physics changes.",
            Version="0.1.0", Website="", ModTags=new[]{"Skeleton"},
        },options));
        var files=new Dictionary<string,string>{[gamePath]="files/skeleton.sklb"};
        if(est?.SourceGamePath is{} actualPath)files[actualPath]="files/skeleton.sklb";
        File.WriteAllText(Path.Combine(destinationDirectory,"default_mod.json"),JsonSerializer.Serialize(new
        {
            Name="", Description="", Priority=0,
            Files=files,
            FileSwaps=new Dictionary<string,string>(), Manipulations=est is null?Array.Empty<object>():new object[]{new{Type="Est",Manipulation=new{Entry=est.SkeletonId,Gender="Female",Race="Midlander",SetId=est.SetId,Slot="Body"}}},
        },options));
        ZipFile.CreateFromDirectory(destinationDirectory,destinationDirectory+".pmp");
        return destinationDirectory+".pmp";
    }
}
