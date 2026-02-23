namespace SimpleDroneGCS.Models
{
    
    public class Waypoint
    {
        public int Sequence { get; set; }         
        public double Latitude { get; set; }      
        public double Longitude { get; set; }     
        public double Altitude { get; set; }      
        public double Radius { get; set; }        

        public ushort Command { get; set; }       
        public byte Frame { get; set; }           
        public byte Current { get; set; }         
        public byte Autocontinue { get; set; }    

        public float Param1 { get; set; }         
        public float Param2 { get; set; }         
        public float Param3 { get; set; }         
        public float Param4 { get; set; }         

        public Waypoint()
        {
            Radius = 10.0;                        
            Command = 16;                         
            Frame = 3;                            
            Autocontinue = 1;                     
            Param1 = 0;                           
            Param2 = 5;                           
            Param3 = 0;                           
            Param4 = 0;                           
        }

        public double DistanceTo(Waypoint other)
        {
            double R = 6371000; 
            double dLat = ToRadians(other.Latitude - this.Latitude);
            double dLon = ToRadians(other.Longitude - this.Longitude);
            double a = System.Math.Sin(dLat / 2) * System.Math.Sin(dLat / 2) +
                       System.Math.Cos(ToRadians(this.Latitude)) * System.Math.Cos(ToRadians(other.Latitude)) *
                       System.Math.Sin(dLon / 2) * System.Math.Sin(dLon / 2);
            double c = 2 * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1 - a));
            return R * c;
        }

        private double ToRadians(double degrees)
        {
            return degrees * System.Math.PI / 180.0;
        }

        public override string ToString()
        {
            return $"WP{Sequence}: {Latitude:F6}, {Longitude:F6}, ALT={Altitude:F1}m";
        }
    }
}