using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ClothingSkeleton.Core;

/// <summary>Appends to a fresh XML serialization of the trusted original HKX.</summary>
public static class HavokXmlAppend
{
    public static string ApplyAnchors(string trustedTemplateXml,SkeletonDocument baseline,SkeletonDocument candidate,EquipmentAnchorPlan plan)
    {
        if(plan.Info.ProjectId!=baseline.ProjectId || plan.Info.SourceSha256!=baseline.SourceSha256 ||
            plan.AddedAnchors.Count!=candidate.AnchorBindings.Count || candidate.AnchorSchemaVersion!=1)
            throw new InvalidDataException("�ҵ�ƻ��뷵����Ŀ��ƥ�䡣");
        var xml=XDocument.Parse(Apply(trustedTemplateXml,baseline,candidate),LoadOptions.PreserveWhitespace);
        var skeleton=xml.Root!.Elements("object").Single(e=>(string?)e.Attribute("type")=="hkaSkeleton");
        XElement Array(string name)=>skeleton.Elements("array").Single(e=>(string?)e.Attribute("name")==name);
        var bones=Array("bones");var poses=Array("referencePose");var parents=Array("parentIndices");
        var oldBones=bones.Elements("struct").Take(baseline.Bones.Count).Select(e=>new XElement(e)).ToArray();
        var oldPoses=poses.Elements("vec12").Take(baseline.Bones.Count).Select(e=>new XElement(e)).ToArray();
        bones.RemoveNodes();poses.RemoveNodes();
        foreach(var b in plan.NativeBones)
        {
            if(b.OriginalIndex>=0){bones.Add(new XElement(oldBones[b.OriginalIndex]));poses.Add(new XElement(oldPoses[b.OriginalIndex]));continue;}
            var bone=new XElement("struct",new XElement("string",new XAttribute("name","name"),b.Name));
            if(b.LockTranslation!=0)bone.Add(new XElement("byte",new XAttribute("name","lockTranslation"),b.LockTranslation));
            bones.Add(bone);
            float[] values=[b.Translation.X,b.Translation.Y,b.Translation.Z,b.TranslationW,b.Rotation.X,b.Rotation.Y,b.Rotation.Z,b.Rotation.W,b.Scale.X,b.Scale.Y,b.Scale.Z,b.ScaleW];
            poses.Add(new XElement("vec12",string.Join(" ",values.Select(v=>"x"+BitConverter.SingleToUInt32Bits(v).ToString("x8",CultureInfo.InvariantCulture)))));
        }
        var index=plan.NativeBones.Select((b,i)=>(b.Id,i)).ToDictionary(p=>p.Id,p=>p.i);
        bones.SetAttributeValue("size",plan.NativeBones.Count);poses.SetAttributeValue("size",plan.NativeBones.Count);parents.SetAttributeValue("size",plan.NativeBones.Count);
        parents.Value=string.Join(" ",plan.NativeBones.Select(b=>b.ParentId is{} p?index[p]:-1));
        return xml.ToString(SaveOptions.DisableFormatting);
    }

    public static string Apply(string trustedTemplateXml, SkeletonDocument baseline, SkeletonDocument candidate,EquipmentAttachment? attachment=null)
    {
        var ordered = SkeletonValidator.OrderedForAppend(candidate, baseline);
        if(candidate.AttachmentTargetBone is{} target && (attachment is null || attachment.Target.BoneName!=target) ||
            attachment is not null && (candidate.AttachmentTargetBone!=attachment.Target.BoneName ||
                attachment.Info.ProjectId!=baseline.ProjectId || attachment.Info.SourceGamePath!=baseline.SourceGamePath || attachment.Info.SourceSha256!=baseline.SourceSha256))
            throw new InvalidDataException("����ҽӱ�������ԭ��Ǽ����ɲ�У��ҽӼƻ���");
        if (baseline.MapperCount != 0 || baseline.LocalFrameCount != 0 || baseline.PartitionCount != 0 || baseline.ExtraVariantCount != 0)
            throw new InvalidDataException("This append prototype supports simple extra-skeleton templates only. Mapper/partition/local-frame editing has not been validated.");
        using var reader = XmlReader.Create(new StringReader(trustedTemplateXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (xml.Root?.Name != "hktagfile" || (string?)xml.Root.Attribute("sdkversion") != "hk_2014.2.5-r1")
            throw new InvalidDataException("Unsupported Havok XML dialect.");
        var skeletons = xml.Root.Elements("object").Where(e => (string?)e.Attribute("type") == "hkaSkeleton").ToList();
        if (skeletons.Count != 1) throw new InvalidDataException("Ambiguous XML skeleton.");
        var skeleton = skeletons[0];
        XElement Array(string name) => skeleton.Elements("array").Single(e => (string?)e.Attribute("name") == name);
        var bones = Array("bones");
        var poses = Array("referencePose");
        var parents = Array("parentIndices");
        var originalNames = bones.Elements("struct").Select(e => e.Elements("string").Single(s => (string?)s.Attribute("name") == "name").Value);
        if (!originalNames.SequenceEqual(baseline.Bones.OrderBy(b => b.OriginalIndex).Select(b => b.Name)) || poses.Elements("vec12").Count() != baseline.Bones.Count)
            throw new InvalidDataException("Template and baseline do not match.");
        var index = ordered.Select((b, i) => (b.Id, i)).ToDictionary(p => p.Id, p => p.i);
        XElement Pose(BoneRecord bone)
        {
            float[] values = [bone.Translation.X, bone.Translation.Y, bone.Translation.Z, bone.TranslationW,
                bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W,
                bone.Scale.X, bone.Scale.Y, bone.Scale.Z, bone.ScaleW];
            return new XElement("vec12", string.Join(" ", values.Select(v => "x" + BitConverter.SingleToUInt32Bits(v).ToString("x8", CultureInfo.InvariantCulture))));
        }
        if(attachment?.Changed==true)poses.Elements("vec12").First().ReplaceWith(Pose(attachment.Materialize(ordered)[0]));
        foreach (var bone in ordered.Skip(baseline.Bones.Count))
        {
            var entry = new XElement("struct", new XElement("string", new XAttribute("name", "name"), bone.Name));
            if (bone.LockTranslation != 0) entry.Add(new XElement("byte", new XAttribute("name", "lockTranslation"), bone.LockTranslation));
            bones.Add(entry);
            poses.Add(Pose(bone));
        }
        bones.SetAttributeValue("size", ordered.Count);
        poses.SetAttributeValue("size", ordered.Count);
        parents.SetAttributeValue("size", ordered.Count);
        parents.Value = string.Join(" ", ordered.Select(b => b.ParentId is { } p ? index[p] : -1));
        return xml.ToString(SaveOptions.DisableFormatting);
    }
}
