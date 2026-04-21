using System;
using GMap.NET;
using GMap.NET.MapProviders;

namespace SimpleDroneGCS.Services
{
    public class OpenTopoMapProvider : OpenStreetMapProviderBase
    {
        public static readonly OpenTopoMapProvider Instance;

        static OpenTopoMapProvider()
        {
            Instance = new OpenTopoMapProvider();
        }

        OpenTopoMapProvider()
        {
            RefererUrl = "https://opentopomap.org/";
            Copyright = "© OpenTopoMap (CC-BY-SA), © OpenStreetMap contributors";
        }

        private static readonly Guid _id = new("D5F2C1E0-8B4A-4F1D-9E2C-7A1B3D4E5F60");
        public override Guid Id => _id;
        public override string Name => "OpenTopoMap";

        private GMapProvider[] _overlays;
        public override GMapProvider[] Overlays => _overlays ??= new GMapProvider[] { this };

        public override PureImage GetTileImage(GPoint pos, int zoom)
        {
            char server = (char)('a' + (int)((pos.X + pos.Y) % 3));
            string url = $"https://{server}.tile.opentopomap.org/{zoom}/{pos.X}/{pos.Y}.png";
            return GetTileImageUsingHttp(url);
        }
    }   
}