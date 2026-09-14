using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Data
{
    /// bd `app-w4ca` (spec §3.5а): the carrier that brings one body's baked
    /// `PoseTable` from the asset database into `SimConfigBuilder`. It holds
    /// the table and nothing else — no behavior, no second copy of any number.
    ///
    /// ⛔ WHY A CARRIER AT ALL, rather than the table sitting on `HeroConfig` /
    /// `MobConfig` directly: the table is BAKED, not authored. Put it on the
    /// balance asset and the owner's hot-tweak of a radius would be editing a
    /// file the baker overwrites, and Unity's inspector would invite exactly
    /// that. A separate asset makes "these numbers are generated" visible in
    /// the project window instead of only in a comment.
    ///
    /// ⛔⛔ AND IT IS NOT A `.asset`: it is the import result of a
    /// `.posetable`, produced by `PoseTableImporter`. A native `.asset` carries
    /// no `ScriptedImporter`, so it could not depend on the source `.fbx` —
    /// and that dependency IS the rebuild mechanism (decision Н50, "the
    /// animations will change, provide for it"). Nothing may create one of
    /// these by hand; `ScriptableObject.CreateInstance` in the test fixtures is
    /// the single exception, and there it carries a fixture table on purpose.
    public class PoseTableAsset : ScriptableObject
    {
        /// ⛔ A FIELD, NOT A PROPERTY, and public rather than
        /// `[SerializeField] private`: `SimConfigBuilder` copies it by `in`,
        /// and the test fixtures take a `ref` to it so a change made through
        /// the reference is the change the config build sees. A property would
        /// hand out copies and the fixtures would pin nothing.
        public PoseTable Table;

        // ⚠ THERE IS NO `Checksum` PROPERTY HERE, AND THAT IS THE POINT: the
        // seal lives inside `Table` (`PoseTable.Checksum`), the config build
        // recomputes it over the loaded numbers and compares (validation rule
        // 27), and a forwarding property on this wrapper would be a cache of a
        // cache with nobody reading it.
    }
}
