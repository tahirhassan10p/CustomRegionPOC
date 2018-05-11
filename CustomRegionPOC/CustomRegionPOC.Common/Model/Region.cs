using Amazon.DynamoDBv2.DataModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace CustomRegionPOC.Common.Model
{
    [DynamoDBTable("tile")]
    public class Region
    {
        public string Name { get; set; }

        public string Tile { get; set; }

        public string LocationPoints { get; set; }

        public DateTime CreateDate { get; set; }

        public List<LocationPoint> Points { get; set; }

        public List<Tile> Tiles { get; set; }
    }
}
