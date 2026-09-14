using System.Collections.Generic;
using System.IO;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Ring.Editor
{
    /// bd `app-w4ca` (spec §3.5, integrity level 1): THE FIRST
    /// `ScriptedImporter` IN THIS PROJECT, and the cost of that is named rather
    /// than discovered.
    ///
    /// WHY ONE IS NEEDED AT ALL. The defense against a table that no longer
    /// matches the clips it was baked from is a REBUILD, and a rebuild needs
    /// two things Unity gives only to a scripted importer: a VERSION field
    /// (changing it re-imports every asset of this type) and
    /// `AssetImportContext.DependsOnSourceAsset` (touching the source re-imports
    /// the dependent). That IS decision Н50 — "the animations will change,
    /// provide for it" — as a mechanism rather than as a discipline. A native
    /// `.asset` carries no importer and could depend on nothing.
    ///
    /// ⛔ IT DOES NOT BAKE, AND THAT IS DELIBERATE. The project's convention for
    /// generating data is an idempotent command under `Ring/…`, and `PoseBaker`
    /// stays one. This importer only turns the committed bytes into a
    /// `PoseTableAsset` and holds the dependency edge.
    ///
    /// ⛔⛔ THE SECOND COST, ALSO NAMED: `.posetable` IS BINARY, so a change to
    /// it is INVISIBLE IN A PULL REQUEST — the diff shows an LFS pointer. That
    /// is why the "rebake and compare" gate is mandatory rather than desirable:
    /// it is the only reader a reviewer has.
    ///
    /// ⚠ DETERMINISM IS A REQUIREMENT OF THE IMPORT PIPELINE, not just good
    /// manners (Unity's own "asset import determinism" note): the same bytes
    /// must produce the same asset, or the pipeline caches a result that is
    /// wrong. Nothing here reads the clock, the scene or the project settings.
    [ScriptedImporter(Version, Extension)]
    public class PoseTableImporter : ScriptedImporter
    {
        public const string Extension = "posetable";

        /// ⛔ BUMPING THIS RE-IMPORTS EVERY `.posetable` — that is what it is
        /// for. Raise it whenever the layout below changes, never for a change
        /// in the baker's numbers (those travel in the bytes).
        public const int Version = 1;

        /// Four bytes at the head of the file so a truncated or foreign file is
        /// refused by NAME instead of read as nonsense.
        const uint Magic = 0x52505431; // "RPT1"

        public override void OnImportAsset(AssetImportContext ctx)
        {
            PoseTable table = Read(File.ReadAllBytes(ctx.assetPath));

            var asset = ScriptableObject.CreateInstance<PoseTableAsset>();
            asset.Table = table;
            asset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);

            ctx.AddObjectToAsset("pose table", asset);
            ctx.SetMainObject(asset);

            // ⛔ THE DEPENDENCY EDGE, AND IT IS THE WHOLE POINT OF THIS CLASS:
            // touch a source `.fbx` and this artifact is re-imported, so a
            // stale table shows up as a re-import rather than as a body whose
            // hit volumes stand in yesterday's pose.
            foreach (string fbx in SourceModels())
                ctx.DependsOnSourceAsset(fbx);
        }

        /// The `.fbx` files the five tables are baked from. ⚠ Taken from the
        /// prefabs' own model references rather than written out: a path list
        /// here would be a sixth home of "where the bodies live".
        static IEnumerable<string> SourceModels()
        {
            var seen = new HashSet<string>();
            foreach (AnimatorCatalog.BodyEntry body in AnimatorCatalog.Bodies)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(body.PrefabPath);
                if (prefab == null) continue;
                foreach (Object dep in EditorUtility.CollectDependencies(new Object[] { prefab }))
                {
                    if (dep == null) continue;
                    string path = AssetDatabase.GetAssetPath(dep);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add(path)) yield return path;
                }
            }
        }

        // ---- the format ------------------------------------------------------
        //
        // ⛔ ITS ONE HOME IS HERE, read by `Read` and written by `Write`, both
        // in this file: a writer and a reader of one layout that live apart are
        // two chances to disagree about a byte, and the disagreement surfaces
        // as garbage geometry rather than as an error.
        //
        //   magic  uint32        "RPT1"
        //   int32  BoneCount
        //   int32  clipCount     (ClipFirstRow has clipCount + 1 entries)
        //   int32  ClipFirstRow[clipCount + 1]
        //   int32  rowCount
        //   float  Bones[rowCount * BoneCount * 3]
        //   int32  thresholdCount, float BlendThresholds[...]
        //   int32  maskCount,      uint64 UpperLayerMask[...]
        //   uint64 Checksum
        //
        // Little-endian throughout (`BinaryWriter`'s own order), which is the
        // order every machine this project builds on uses.

        public static void Write(string assetPath, in PoseTable table)
        {
            using (var stream = new MemoryStream())
            {
                using (var w = new BinaryWriter(stream))
                {
                    w.Write(Magic);
                    w.Write(table.BoneCount);
                    w.Write(table.ClipFirstRow.Length - 1);
                    for (int i = 0; i < table.ClipFirstRow.Length; i++) w.Write(table.ClipFirstRow[i]);
                    w.Write(table.Bones.Length);
                    for (int i = 0; i < table.Bones.Length; i++)
                    {
                        w.Write(table.Bones[i].x);
                        w.Write(table.Bones[i].y);
                        w.Write(table.Bones[i].z);
                    }
                    w.Write(table.BlendThresholds.Length);
                    for (int i = 0; i < table.BlendThresholds.Length; i++)
                        w.Write(table.BlendThresholds[i]);
                    w.Write(table.UpperLayerMask.Length);
                    for (int i = 0; i < table.UpperLayerMask.Length; i++)
                        w.Write(table.UpperLayerMask[i]);
                    w.Write(table.Checksum);
                }
                File.WriteAllBytes(assetPath, stream.ToArray());
            }
        }

        public static PoseTable Read(byte[] bytes)
        {
            using (var r = new BinaryReader(new MemoryStream(bytes)))
            {
                uint magic = r.ReadUInt32();
                if (magic != Magic)
                    throw new System.ArgumentException(
                        $"not a pose table: magic 0x{magic:X8}, expected 0x{Magic:X8}");

                var table = new PoseTable { BoneCount = r.ReadInt32() };
                int clipCount = r.ReadInt32();
                table.ClipFirstRow = new int[clipCount + 1];
                for (int i = 0; i < table.ClipFirstRow.Length; i++)
                    table.ClipFirstRow[i] = r.ReadInt32();

                table.Bones = new float3[r.ReadInt32()];
                for (int i = 0; i < table.Bones.Length; i++)
                    table.Bones[i] = new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                table.BlendThresholds = new float[r.ReadInt32()];
                for (int i = 0; i < table.BlendThresholds.Length; i++)
                    table.BlendThresholds[i] = r.ReadSingle();

                table.UpperLayerMask = new ulong[r.ReadInt32()];
                for (int i = 0; i < table.UpperLayerMask.Length; i++)
                    table.UpperLayerMask[i] = r.ReadUInt64();

                table.Checksum = r.ReadUInt64();
                return table;
            }
        }
    }
}
