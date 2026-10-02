#define NAVGEN

using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using CEM.Graphic.Geometry;
using System.Collections;
using System.Threading;
using CEM.Graphic;
using System.Drawing;
using OpenTK;
using CEM.Graphic.Rendering;
using CEM.Utils;

namespace MNL {
  public class NiFile {
    public const uint INVALID_REF = 0xFFFFFFFF;
    public const string CMD_TOP_LEVEL_OBJECT = "Top Level Object";
    public const string CMD_END_OF_FILE = "End Of File";

    public eNifVersion Version {
      get { return Header.Version; }
    }

    public string FileName {
      get; private set;
    }

    public NiHeader Header;
    public NiFooter Footer;
    public Dictionary<uint, NiObject> ObjectsByRef;
    public bool Loaded = false;

    private NiAVObject _root;
    private Vector3 _min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
    private Vector3 _max = new Vector3(float.MinValue, float.MinValue, float.MinValue);


    // cache node types - its faster then getting the type each time... (way faster)
    private static Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

    // private BoundingBox _boundingBox;

    public BoundingBox BoundingBox {
      get {
        return new BoundingBox(_min, _max);
      }
    }

    public NiFile(BinaryReader reader, string fileName) {
      this.FileName = fileName;
#if NAVGEN
      LoadNiFile(reader);
#else
      GUI.ScheduleBackgroundThread(() => LoadNiFile(reader));
#endif
    }

    private void LoadNiFile(BinaryReader reader) {

      try
      {
        Header = new NiHeader(this, reader);
      }
      catch (InvalidOperationException e)
      {
        Log.Error(e.Message);
        return;
      }

      ReadNiObjects(reader);
      Footer = new NiFooter(this, reader);
      reader.Dispose();
      FixRefs();
      _root = FindRoot();

      //todo: maybe move this out?
#if NAVGEN
      GenerateMeshs();
      Loaded = true;
#else
      GUI.ScheduleGLThread(() => {
        GenerateMeshs();
        _loaded = true;
      });
#endif
    }

    ~NiFile() {
      DestroyMeshs();
    }

    private void ReadNiObjects(BinaryReader reader) {
      ObjectsByRef = new Dictionary<uint, NiObject>();

      var i = 0;
      while (true) {
        var type = "";
        if (Version >= eNifVersion.VER_5_0_0_1) {
          if (Version <= eNifVersion.VER_10_1_0_106) {
            if (reader.ReadUInt32() != 0) {
              throw new Exception("Check value is not zero! Invalid file?");
            }
          }
          type = Header.BlockTypes[Header.BlockTypeIndex[i]].Value;
        } else {
          var objTypeStrLen = reader.ReadUInt32();
          if (objTypeStrLen > 30 || objTypeStrLen < 6) {
            throw new Exception("Invalid object type string length!");
          }
          type = new string(reader.ReadChars((int)objTypeStrLen));

          if (Header.Version < eNifVersion.VER_3_3_0_13) {
            if (type == CMD_TOP_LEVEL_OBJECT)
              continue;

            if (type == CMD_END_OF_FILE)
              break;
          }
        }

        uint index;
        if (Version < eNifVersion.VER_3_3_0_13) {
          index = reader.ReadUInt32();
        } else {
          index = (uint)i;
        }

        Type csType = null;
        lock (_typeCache) {
          if (!_typeCache.TryGetValue(type, out csType)) {
            csType = Type.GetType(GetType().Namespace + "." + type);
            _typeCache.Add(type, csType);
          }
        }


        if (csType == null) {
          throw new NotImplementedException(type);
        }

        var currentObject = (NiObject)Activator.CreateInstance(csType, new object[] { this, reader });
        ObjectsByRef.Add(index, currentObject);

        if (Version < eNifVersion.VER_3_3_0_13)
          continue;

        i++;

        if (i >= Header.NumBlocks)
          break;
      }
    }

    private void FixRefs() {
      foreach (var obj in ObjectsByRef.Values) {
        FixRefs(obj);

        NiTexturingProperty prop = obj as NiTexturingProperty;
        if (prop != null) {
          FixRefs(prop.BaseTexture);
          FixRefs(prop.DarkTexture);
          FixRefs(prop.DetailTexture);
          FixRefs(prop.GlossTexture);
          FixRefs(prop.GlowTexture);
          FixRefs(prop.BumpMapTexture);
          FixRefs(prop.Decal0Texture);
          FixRefs(prop.Decal1Texture);
          FixRefs(prop.Decal2Texture);
          FixRefs(prop.Decal3Texture);

          if (prop.ShaderTextures != null) {
            foreach (var desc in prop.ShaderTextures) {
              FixRefs(desc);
            }
          }
        }
      }
    }

    private void FixRefs(object obj) {
      if (obj == null)
        return;

      foreach (var field in obj.GetType().GetFields()) {
        if (field.FieldType.Name.Contains("NiRef")) {
          if (field.FieldType.IsArray) {
            var values = (IEnumerable)field.GetValue(obj);
            if (values == null)
              continue;
            foreach (dynamic val in values) {
              val.SetRef(this);

              if (field.Name == "Children") {
                if (val.IsValid()) {
                  var child = val.Object as NiAVObject;
                  if (child == null) {
                    throw new Exception("no child");
                  }
                  child.Parent = (NiNode)obj;
                }
              }
            }
          } else {
            dynamic value = field.GetValue(obj);
            if (value == null)
              continue;
            value.SetRef(this);
          }
        }
      }
    }

    private NiAVObject FindRoot() {
      var avObj = ObjectsByRef.Values.OfType<NiAVObject>().Select(obj => obj).FirstOrDefault();

      if (avObj == null)
        return null;

      while (avObj.Parent != null)
        avObj = avObj.Parent;

      return avObj;
    }

    private void PrintNifTree() {
      var root = FindRoot();
      if (root == null) {
        Console.WriteLine("No Root!");
        return;
      }
      var depth = 0;
      PrintNifNode(root, depth);
    }

    private void PrintNifNode(NiAVObject root, int depth) {
      var ds = "";

      for (var i = 0; i < depth; i++)
        ds += "*";

      ds += " ";

      Console.WriteLine(ds + " " + root.Name);

      var niNode = root as NiNode;
      if (niNode != null) {
        foreach (var child in niNode.Children) {
          if (child.IsValid())
            PrintNifNode(child.Object, depth + 1);
        }
      }
    }
    public void GenerateMeshs() {
      Matrix4 transform = Matrix4.Identity;
      GenerateMesh(_root, transform);
    }

    private void GenerateMesh(NiAVObject node, Matrix4 transform) {
      transform = Matrix4.CreateTranslation(node.Translation) * transform;
      transform = node.Rotation * transform;
      transform = Matrix4.CreateScale(node.Scale) * transform;

      var niGeom = node as MNL.NiTriBasedGeometry;
      if (niGeom != null) {
        niGeom.Transform = transform;
        var min = Vector3.TransformVector(niGeom.Data.Object.Center - new Vector3(niGeom.Data.Object.Radius, niGeom.Data.Object.Radius, niGeom.Data.Object.Radius), transform);
        var max = Vector3.TransformVector(niGeom.Data.Object.Center + new Vector3(niGeom.Data.Object.Radius, niGeom.Data.Object.Radius, niGeom.Data.Object.Radius), transform);

        _min.X = Math.Min(_min.X, min.X);
        _min.Y = Math.Min(_min.Y, min.Y);
        _min.Z = Math.Min(_min.Z, min.Z);

        _max.X = Math.Max(_max.X, max.X);
        _max.Y = Math.Max(_max.Y, max.Y);
        _max.Z = Math.Max(_max.Z, max.Z);

        if (niGeom.Mesh == null) {
          // todo: improve this xD
          bool hasBaseTexture = false;
          foreach (var p in niGeom.Properties) {
            if (p.Object is NiTexturingProperty && (((NiTexturingProperty)p.Object).BaseTexture != null || ((NiTexturingProperty)p.Object).ShaderTextures != null)) {
              hasBaseTexture = true;
            }
          }
          if (hasBaseTexture && niGeom.Data.Object.UVSets.Length > 0) {
            var strips = niGeom as NiTriStrips;
            if (strips != null) {
              niGeom.Mesh = new CEM.Graphic.NiTriStripsMesh(strips);
            } else {
              var shape = niGeom as NiTriShape;
              if (shape != null) {
                niGeom.Mesh = new CEM.Graphic.NiTriShapeMesh(shape);
              }
            }
          }
        }
      }

      var niNode = node as MNL.NiNode;
      if (niNode != null) {
        foreach (var child in niNode.Children) {
          if (child.IsValid())
            GenerateMesh(child.Object, transform);
        }
      }
    }

    public void DestroyMeshs() {
      DestroyMesh(_root);
    }

    private void DestroyMesh(NiAVObject node) {
      var niGeom = node as MNL.NiTriBasedGeometry;

      if (niGeom != null) {
        if (niGeom.Mesh != null) {
          niGeom.Mesh = null;
        }
      }

      var niNode = node as MNL.NiNode;
      if (niNode != null) {
        foreach (var child in niNode.Children) {
          if (child.IsValid())
            DestroyMesh(child.Object);
        }
      }
    }

    public void Render(Vector3 translation, Vector3 cameraPosition) {
      if (!Loaded)
        return;

      float distance = 0;
      NiMesh.Begin();
      RenderNode(_root, distance);
    }

    private void RenderNode(MNL.NiAVObject node, float distance) {
      var niGeom = node as MNL.NiTriBasedGeometry;
      if (niGeom != null && niGeom.Mesh != null) {
        GL.PushMatrix();
        GL.MultMatrix(ref niGeom.Transform);
        niGeom.Mesh.BeginRender();
        niGeom.Mesh.Render();
        GL.PopMatrix();
      }

      if (node is NiLODNode) {
        var lod = node as NiLODNode;

        if (lod.LODLevels != null) {
          for (int i = 0; i < lod.LODLevels.Length; i++) {
            if (distance >= lod.LODLevels[i].NearExtent && distance < lod.LODLevels[i].FarExtent) {
              RenderNode(lod.Children[i].Object, distance);
            }
          }
        }

        if (lod.LODLevelData != null) {
          if (lod.LODLevelData.Object is NiRangeLODData) {
            var rangeLod = lod.LODLevelData.Object as NiRangeLODData;
            for (int i = 0; i < rangeLod.LODLevels.Length; i++) {
              if (distance >= rangeLod.LODLevels[i].NearExtent && distance < rangeLod.LODLevels[i].FarExtent) {
                RenderNode(lod.Children[i].Object, distance);
              }
            }
          }

          //NiRangeLodData
          //NiScreenLodData
        }
      } else {
        var niNode = node as MNL.NiNode;
        if (niNode != null) {
          foreach (var child in niNode.Children) {
            RenderNode(child.Object, distance);
          }
        }
      }
    }

    public void RenderGroup(RenderState state, IEnumerable<Matrix4> transforms) {
      if (!Loaded)
        return;

      NiMesh.Begin();
      RenderGroupNode(_root, transforms);
    }

    private void RenderGroupNode(MNL.NiAVObject node, IEnumerable<Matrix4> transforms) {
      float distance = 1; // haxx for lod :>

      var niGeom = node as MNL.NiTriBasedGeometry;
      if (niGeom != null && niGeom.Mesh != null) {
        niGeom.Mesh.BeginRender();
        foreach (var objTransform in transforms) {
          GL.PushMatrix();
          Matrix4 nodeTransform = niGeom.Transform * objTransform;
          GL.MultMatrix(ref nodeTransform);
          niGeom.Mesh.Render();
          GL.PopMatrix();
        }
        niGeom.Mesh.EndRender();
      }

      var niNode = node as MNL.NiNode;
      if (niNode != null) {
        var lod = node as MNL.NiLODNode;
        if (lod != null) {
          if (lod.LODLevels != null) {
            for (int i = 0; i < lod.LODLevels.Length; i++) {
              if (distance >= lod.LODLevels[i].NearExtent && distance < lod.LODLevels[i].FarExtent) {
                RenderGroupNode(lod.Children[i].Object, transforms);
              }
            }
          }

          if (lod.LODLevelData != null) {
            if (lod.LODLevelData.Object is NiRangeLODData) {
              var rangeLod = lod.LODLevelData.Object as NiRangeLODData;
              for (int i = 0; i < rangeLod.LODLevels.Length; i++) {
                if (distance >= rangeLod.LODLevels[i].NearExtent && distance < rangeLod.LODLevels[i].FarExtent) {
                  RenderGroupNode(lod.Children[i].Object, transforms);
                }
              }
            }
          }
        } else {
          foreach (var child in niNode.Children) {
            RenderGroupNode(child.Object, transforms);
          }
        }
      }
    }
  }
}
