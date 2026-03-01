using System;

namespace NINA.Plugins.PlateSolvePlus.Models {

    public sealed class AscomDeviceInfo {
        public string Name { get; }
        public string ProgId { get; }

        public AscomDeviceInfo(string name, string progId) {
            Name = name ?? "";
            ProgId = progId ?? "";
        }

        public override string ToString() => $"{Name} ({ProgId})";
    }
}
