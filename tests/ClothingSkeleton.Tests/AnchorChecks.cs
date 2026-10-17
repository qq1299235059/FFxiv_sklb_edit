using System.Buffers.Binary;
using System.Numerics;
using System.Xml.Linq;
using ClothingSkeleton.Core;

internal static class AnchorChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject,string directory)
    {
        var bytes=new byte[112];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x736b6c62);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4),0x3030);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6),0x3133);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8),48);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12),80);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20),201);
        for(var i=0;i<4;i++)BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24+4*i),-1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48),0x616c7068);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(52),1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(54),8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56),25);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(60),2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(64),1);
        var original=SklbContainer.Parse(bytes);
        var root=new BoneRecord{Name="n_ex_top",OriginalIndex=0};
        var cloth=new BoneRecord{Name="j_ex_original",OriginalIndex=1,ParentId=root.Id,Translation=new(0,.1f,0)};
        var baseline=new SkeletonDocument{Name="test",SourceSha256=original.Sha256,SourceGamePath="chara/human/c0201/skeleton/top/t0100/skl_c0201t0100.sklb",Bones=[root,cloth]};
        var bodyRoot=new BoneRecord{Name="n_hara",OriginalIndex=0};
        var left=new BoneRecord{Name="j_te_l",OriginalIndex=1,ParentId=bodyRoot.Id,Translation=new(1,1,0),Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.5f)};
        var right=new BoneRecord{Name="j_te_r",OriginalIndex=2,ParentId=bodyRoot.Id,Translation=new(-1,1,0)};
        var third=new BoneRecord{Name="j_extra_a",OriginalIndex=3,ParentId=bodyRoot.Id,Translation=new(0,1,0)};
        var fourth=new BoneRecord{Name="j_extra_b",OriginalIndex=4,ParentId=bodyRoot.Id,Translation=new(0,2,0)};
        var bodyContainer=SklbContainer.Parse(SklbLayerTable.Rebuild(original,original.Payload.Span,[new(4,[1,3]),new(5,[2,4]),new(6,[0])],5));
        var body=new SkeletonDocument{Name="body",SourceSha256=bodyContainer.Sha256,SourceGamePath="chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb",Bones=[bodyRoot,left,right,third,fourth]};
        BoneRecord Anchor(BoneRecord b)=>b with{Id=Guid.NewGuid(),OriginalIndex=-1,ParentId=null};
        var al=Anchor(left);var ar=Anchor(right);
        var nl=new BoneRecord{Name="csw_left",ParentId=al.Id,Translation=new(0,.04f,0)};
        var nr=new BoneRecord{Name="csw_right",ParentId=ar.Id,Translation=new(0,.04f,0)};
        var tip=new BoneRecord{Name="csw_left_tip",ParentId=nl.Id,Translation=new(0,.04f,0)};
        var candidate=baseline with{AnchorSchemaVersion=1,Bones=[root,cloth,al,ar,nl,nr,tip],AnchorBindings=[new(al.Id,left.Name,1),new(ar.Id,right.Name,2)]};
        EquipmentAnchorPlan Compile(SkeletonDocument value)=>EquipmentAnchorPlan.Create(original,baseline,value,body,bodyContainer);
        var plan=Compile(candidate);
        check(plan.NativeBones.Take(3).Select(b=>b.Id).SequenceEqual(new[]{root.Id,al.Id,ar.Id}),"multi-attachment roots precede remapped equipment children");
        check(plan.NativeBones.Single(b=>b.Id==root.Id)==root && plan.NativeBones.Single(b=>b.Id==cloth.Id)==cloth,"both hand anchors preserve original equipment root and child rest poses");
        check(plan.NativeBones.Single(b=>b.Id==nl.Id).ParentId==al.Id && plan.NativeBones.Single(b=>b.Id==nr.Id).ParentId==ar.Id,"two independent hand chains retain different parents");
        check(!plan.OriginalIndicesPreserved && plan.NativeBones.FindIndex(b=>b.Id==cloth.Id)==3,"original index relocation is explicit for a roots-first skeleton");
        var result=SklbContainer.Parse(plan.Build(original,original.Payload.Span));
        check(result.VersionLow==0x3031 && Enumerable.Range(0,4).Select(i=>(int)BinaryPrimitives.ReadInt16LittleEndian(result.Header.Span[(40+2*i)..])).SequenceEqual(new[]{0,1,2,-1}),"multi-root wrapper encodes original and two hand connections with a terminator");
        check(result.Header.Span[20..40].SequenceEqual(original.Header.Span[20..40]) && result.Payload.Span.SequenceEqual(original.Payload.Span),"multi-root conversion preserves race, mapper references and payload");
        var layers=SklbLayerTable.Read(result,7);
        check(layers.Select(l=>l.Id).SequenceEqual(new[]{4,5,25}) && layers.Single(l=>l.Id==4).BoneIndices.SequenceEqual(new[]{1}) && layers.Single(l=>l.Id==5).BoneIndices.SequenceEqual(new[]{2}),"used hand anchors inherit their original body layers");
        check(layers.Single(l=>l.Id==25).BoneIndices.Contains(0) && layers.Single(l=>l.Id==25).BoneIndices.Contains(3) && layers.Single(l=>l.Id==25).BoneIndices.Length==5,"original clothing layer membership is remapped and authored nodes are included");
        var path=Path.Combine(directory,"anchors.gltf");GltfExchange.Export(candidate,path);
        var returned=GltfExchange.Import(path,baseline);
        check(returned.AnchorSchemaVersion==1 && returned.AnchorBindings.SequenceEqual(candidate.AnchorBindings),"hashed glTF carries each used anchor identity and body target");
        const string xml="<hktagfile sdkversion='hk_2014.2.5-r1'><class name='unchanged'/><object id='#1' type='hkaSkeleton'><array name='bones' size='2'><struct><string name='name'>n_ex_top</string></struct><struct><string name='name'>j_ex_original</string></struct></array><array name='parentIndices' size='2'>-1 0</array><array name='referencePose' size='2'><vec12>exact-root</vec12><vec12>exact-child</vec12></array></object></hktagfile>";
        var nativeXml=XDocument.Parse(HavokXmlAppend.ApplyAnchors(xml,baseline,candidate,plan));
        var poses=nativeXml.Descendants("vec12").ToArray();
        check(poses[0].Value=="exact-root" && poses[3].Value=="exact-child","roots-first XML retains exact original pose strings at remapped indices");
        check(nativeXml.Descendants("class").Single().Attribute("name")!.Value=="unchanged","multi-anchor XML leaves unrelated objects intact");
        reject(()=>Compile(candidate with{Bones=[root,cloth,al,ar,nr]}),"unused anchor roots are rejected by the native compiler");
        reject(()=>Compile(candidate with{Bones=[root,cloth,al with{Translation=Vector3.Zero},ar,nl,nr,tip]}),"moved body anchors cannot become native attachment roots");
        reject(()=>Compile(candidate with{AnchorBindings=[new(al.Id,left.Name,2),new(ar.Id,right.Name,2)]}),"body index and anchor name mismatch is rejected");
        reject(()=>Compile(candidate with{AnchorBindings=[new(root.Id,left.Name,1)]}),"an anchor cannot claim an original equipment identity");
        reject(()=>Compile(candidate with{Bones=[root,cloth,al,ar,nl with{ParentId=null},nr,tip]}),"an unparented authored chain is rejected");
        reject(()=>Compile(candidate with{AnchorBindings=[new(al.Id,left.Name,1),new(al.Id,left.Name,1)]}),"duplicated anchor mappings are rejected");
        var anchorList=new[]{left,right,third,fourth}.Select(Anchor).ToArray();
        var tooMany=baseline with{AnchorSchemaVersion=1,Bones=baseline.Bones.Concat(anchorList).Concat(anchorList.Select((b,i)=>new BoneRecord{Name="extra_"+i,ParentId=b.Id})).ToList(),
            AnchorBindings=anchorList.Select((b,i)=>new AnchorBinding(b.Id,b.Name,i+1)).ToList()};
        reject(()=>Compile(tooMany),"the verified four-connection format limit cannot overflow silently");
        var ordinary=baseline with{AnchorSchemaVersion=1,Bones=[root,cloth,new BoneRecord{Name="csw_plain",ParentId=cloth.Id}]};
        var plainPlan=Compile(ordinary);
        check(plainPlan.OriginalIndicesPreserved && SklbContainer.Parse(plainPlan.Build(original,original.Payload.Span)).VersionLow==0x3030,"no used body anchors keeps the single-root format and original indices");
        var existing=Anchor(bodyRoot);var newAtExisting=new BoneRecord{Name="csw_alias",ParentId=existing.Id};
        reject(()=>Compile(baseline with{AnchorSchemaVersion=1,Bones=[root,cloth,existing,newAtExisting],AnchorBindings=[new(existing.Id,bodyRoot.Name,0)]}),"existing attachment targets must reuse original equipment roots");
        var corrupt=result.OriginalBytes();BinaryPrimitives.WriteInt16LittleEndian(corrupt.AsSpan(42),-1);
        reject(()=>SklbContainer.Parse(corrupt),"a hole in the multi-root connection prefix is rejected");
    }
}
