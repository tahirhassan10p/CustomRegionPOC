using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using CustomRegionPOC.Common.Model;
using CustomRegionPOC.Common.Service;
using IronPython.Hosting;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Amazon.DynamoDBv2.DocumentModel;

namespace CustomRegionPOC.Service
{
    public class RegionService : IRegionService
    {
        private AmazonDynamoDBClient dynamoDBClient;
        private DynamoDBContext context;

        public RegionService(IConfiguration configuration)
        {
            var credentials = new BasicAWSCredentials(configuration["AWS:AccessKey"], configuration["AWS:SecretKey"]);
            this.dynamoDBClient = new AmazonDynamoDBClient(credentials, RegionEndpoint.USEast1);

            CreateTempTable("tile").Wait();

            context = new DynamoDBContext(this.dynamoDBClient);
        }

        public async Task Create(Region region)
        {
            DynamoDBContext context = new DynamoDBContext(this.dynamoDBClient);

            region.Tiles = generateTiles(region.Points);

            List<Region> regions = this.transformRegion(region);

            Parallel.ForEach(regions, item =>
            {
                context.SaveAsync<Region>(item);
            });
        }

        public async Task<List<Region>> Get(decimal lat, decimal lng)
        {
            return await this.getRegion(lat, lng);
        }

        public async Task WaitUntilTableReady(string tableName)
        {
            bool isTableAvailable = false;
            while (!isTableAvailable)
            {
                Console.WriteLine("Waiting for table to be active...");
                Thread.Sleep(5000);
                var tableStatus = await this.dynamoDBClient.DescribeTableAsync(tableName);
                isTableAvailable = tableStatus.Table.TableStatus == "ACTIVE";
            }
        }

        #region Private Function

        public List<Region> transformRegion(Region region)
        {
            List<Region> regions = new List<Region>();

            List<string> locationPoints = new List<string>();
            foreach (LocationPoint point in region.Points)
            {
                locationPoints.Add(point.Lat + " " + point.Lng);
            }

            foreach (Tile tile in region.Tiles)
            {
                regions.Add(new Region
                {
                    Tile = "(" + tile.Row + "," + tile.Column + ")",
                    Name = region.Name,
                    Points = region.Points,
                    LocationPoints = "((" + string.Join(", ", locationPoints) + "))",
                    CreateDate = DateTime.UtcNow
                });
            }

            return regions;
        }

        private async Task CreateTempTable(string tableName)
        {
            string hashKey = "Tile";
            string sortKey = "CreateDate";

            var tableResponse = await this.dynamoDBClient.ListTablesAsync();
            if (!tableResponse.TableNames.Contains(tableName))
            {
                await this.dynamoDBClient.CreateTableAsync(new CreateTableRequest
                {
                    TableName = tableName,
                    ProvisionedThroughput = new ProvisionedThroughput
                    {
                        ReadCapacityUnits = 3,
                        WriteCapacityUnits = 1
                    },
                    KeySchema = new List<KeySchemaElement>
                    {
                        new KeySchemaElement { AttributeName = hashKey, KeyType = KeyType.HASH },
                        new KeySchemaElement { AttributeName = sortKey, KeyType = KeyType.RANGE }
                    },
                    AttributeDefinitions = new List<AttributeDefinition>
                    {
                        new AttributeDefinition { AttributeName = hashKey, AttributeType = ScalarAttributeType.S },
                        new AttributeDefinition { AttributeName = sortKey, AttributeType = ScalarAttributeType.S }
                    }
                });


                await WaitUntilTableReady(tableName);
            }
        }

        private List<Tile> generateTiles(List<LocationPoint> points)
        {
            List<Tile> actualTiles = new List<Tile>();
            List<Tile> tiles = new List<Tile>();

            Parallel.ForEach(points, point =>
            {
                dynamic coordinates = getCoordinateTile(point.Lat, point.Lng);

                actualTiles.Add(new Tile
                {
                    Row = coordinates["tileX"],
                    Column = coordinates["tileY"]
                });
            });

            int minRow = actualTiles.Min(x => x.Row);
            int maxRow = actualTiles.Max(x => x.Row);

            int minColumn = actualTiles.Min(x => x.Column);
            int maxColumn = actualTiles.Max(x => x.Column);

            for (int i = minRow; i <= maxRow; i++)
            {
                for (int j = minColumn; j <= maxColumn; j++)
                {
                    tiles.Add(new Tile
                    {
                        Row = i,
                        Column = j
                    });
                }
            }

            return tiles;
        }

        private dynamic getCoordinateTile(decimal lat, decimal lng, int zoomlevel = 10)
        {
            #region Python Script
            string script = @"#!/usr/bin/env python

import math

class GlobalMercator(object):
	def __init__(self, tileSize=256):
		'Initialize the TMS Global Mercator pyramid'
		self.tileSize = tileSize
		self.initialResolution = 2 * math.pi * 6378137 / self.tileSize
		# 156543.03392804062 for tileSize 256 pixels
		self.originShift = 2 * math.pi * 6378137 / 2.0
		# 20037508.342789244

	def LatLonToMeters(self, lat, lon ):
		'Converts given lat/lon in WGS84 Datum to XY in Spherical Mercator EPSG:900913'

		mx = lon * self.originShift / 180.0
		my = math.log( math.tan((90 + lat) * math.pi / 360.0 )) / (math.pi / 180.0)

		my = my * self.originShift / 180.0
		return mx, my

	def MetersToLatLon(self, mx, my ):
		'Converts XY point from Spherical Mercator EPSG:900913 to lat/lon in WGS84 Datum'

		lon = (mx / self.originShift) * 180.0
		lat = (my / self.originShift) * 180.0

		lat = 180 / math.pi * (2 * math.atan( math.exp( lat * math.pi / 180.0)) - math.pi / 2.0)
		return lat, lon

	def PixelsToMeters(self, px, py, zoom):
		'Converts pixel coordinates in given zoom level of pyramid to EPSG:900913'

		res = self.Resolution( zoom )
		mx = px * res - self.originShift
		my = py * res - self.originShift
		return mx, my
		
	def MetersToPixels(self, mx, my, zoom):
		'Converts EPSG:900913 to pyramid pixel coordinates in given zoom level'
				
		res = self.Resolution( zoom )
		px = (mx + self.originShift) / res
		py = (my + self.originShift) / res
		return px, py
	
	def PixelsToTile(self, px, py):
		'Returns a tile covering region in given pixel coordinates'

		tx = int( math.ceil( px / float(self.tileSize) ) - 1 )
		ty = int( math.ceil( py / float(self.tileSize) ) - 1 )
		return tx, ty

	def PixelsToRaster(self, px, py, zoom):
		'Move the origin of pixel coordinates to top-left corner'
		
		mapSize = self.tileSize << zoom
		return px, mapSize - py
		
	def MetersToTile(self, mx, my, zoom):
		'Returns tile for given mercator coordinates'
		
		px, py = self.MetersToPixels( mx, my, zoom)
		return self.PixelsToTile( px, py)

	def TileBounds(self, tx, ty, zoom):
		'Returns bounds of the given tile in EPSG:900913 coordinates'
		
		minx, miny = self.PixelsToMeters( tx*self.tileSize, ty*self.tileSize, zoom )
		maxx, maxy = self.PixelsToMeters( (tx+1)*self.tileSize, (ty+1)*self.tileSize, zoom )
		return ( minx, miny, maxx, maxy )

	def TileLatLonBounds(self, tx, ty, zoom ):
		'Returns bounds of the given tile in latutude/longitude using WGS84 datum'

		bounds = self.TileBounds( tx, ty, zoom)
		minLat, minLon = self.MetersToLatLon(bounds[0], bounds[1])
		maxLat, maxLon = self.MetersToLatLon(bounds[2], bounds[3])
		 
		return ( minLat, minLon, maxLat, maxLon )
		
	def Resolution(self, zoom ):
		'Resolution (meters/pixel) for given zoom level (measured at Equator)'
		
		# return (2 * math.pi * 6378137) / (self.tileSize * 2**zoom)
		return self.initialResolution / (2**zoom)
		
	def ZoomForPixelSize(self, pixelSize ):
		'Maximal scaledown zoom of the pyramid closest to the pixelSize.'
		
		for i in range(30):
			if pixelSize > self.Resolution(i):
				return i-1 if i!=0 else 0 # We don't want to scale up

	def GoogleTile(self, tx, ty, zoom):
		'Converts TMS tile coordinates to Google Tile coordinates'
		
		# coordinate origin is moved from bottom-left to top-left corner of the extent
		return tx, (2**zoom - 1) - ty

	def QuadTree(self, tx, ty, zoom ):
		'Converts TMS tile coordinates to Microsoft QuadTree'
		
		quadKey = ''
		ty = (2**zoom - 1) - ty
		for i in range(zoom, 0, -1):
			digit = 0
			mask = 1 << (i-1)
			if (tx & mask) != 0:
				digit += 1
			if (ty & mask) != 0:
				digit += 2
			quadKey += str(digit)
			
		return quadKey



class GlobalGeodetic(object):
	def __init__(self, tileSize = 256):
		self.tileSize = tileSize

	def LatLonToPixels(self, lat, lon, zoom):
		'Converts lat/lon to pixel coordinates in given zoom of the EPSG:4326 pyramid'

		res = 180 / 256.0 / 2**zoom
		px = (180 + lat) / res
		py = (90 + lon) / res
		return px, py

	def PixelsToTile(self, px, py):
		'Returns coordinates of the tile covering region in pixel coordinates'

		tx = int( math.ceil( px / float(self.tileSize) ) - 1 )
		ty = int( math.ceil( py / float(self.tileSize) ) - 1 )
		return tx, ty

	def Resolution(self, zoom ):
		'Resolution (arc/pixel) for given zoom level (measured at Equator)'
		
		return 180 / 256.0 / 2**zoom
		#return 180 / float( 1 << (8+zoom) )

	def TileBounds(tx, ty, zoom):
		'Returns bounds of the given tile'
		res = 180 / 256.0 / 2**zoom
		return (
			tx*256*res - 180,
			ty*256*res - 90,
			(tx+1)*256*res - 180,
			(ty+1)*256*res - 90
		)

class MyClass:
	def go(self, zoomlevel, lat, lon):
		profile = 'mercator'
		latmax = lat
		lonmax = lon
		boundingbox = False
		
		if latmax != None and lonmax != None:
			boundingbox = (lon, lat, lonmax, latmax)
		
		tz = zoomlevel
		mercator = GlobalMercator()

		mx, my = mercator.LatLonToMeters( lat, lon )
		print 'Spherical Mercator (ESPG:900913) coordinates for lat/lon: '
		print (mx, my)
		tminx, tminy = mercator.MetersToTile( mx, my, tz )
		
		if boundingbox:
			mx, my = mercator.LatLonToMeters( latmax, lonmax )
			print 'Spherical Mercator (ESPG:900913) cooridnate for maxlat/maxlon: '
			print (mx, my)
			tmaxx, tmaxy = mercator.MetersToTile( mx, my, tz )
		else:
			tmaxx, tmaxy = tminx, tminy
		
		output = {
			'tileX' : 0,
			'tileY' : 0
		}
		
		for ty in range(tminy, tmaxy+1):
			for tx in range(tminx, tmaxx+1):
				tilefilename = '%s/%s/%s' % (tz, tx, ty)
				print tilefilename, '( TileMapService: z / x / y )'
			
				gx, gy = mercator.GoogleTile(tx, ty, tz)
				
				output = {
					'tileX' : gx,
					'tileY' : gy
				}
				
		return output";

            #endregion

            try
            {
                var engine = Python.CreateEngine();
                var scope = engine.CreateScope();
                var ops = engine.Operations;

                engine.Execute(script, scope);
                var pythonType = scope.GetVariable("MyClass");
                dynamic instance = ops.CreateInstance(pythonType);
                var value = instance.go(zoomlevel, lat, lng);

                return value;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private async Task<List<Region>> getRegion(decimal lat, decimal lng)
        {
            dynamic coordinates = getCoordinateTile(lat, lng);

            List<Region> region = new List<Region>();

            List<ScanCondition> conditions = new List<ScanCondition>();
            conditions.Add(new ScanCondition("Tile", ScanOperator.Contains, ("(" + coordinates["tileX"] + "," + coordinates["tileY"] + ")")));
            region = await context.ScanAsync<Region>(conditions).GetRemainingAsync();

            return filterRegionList(region, lat, lng);
        }

        private List<Region> filterRegionList(List<Region> regions, decimal lat, decimal lng)
        {
            List<Region> filteredRegion = new List<Region>();

            Parallel.ForEach(regions, region =>
            {
                if (pointInPolygon(region, lat, lng))
                {
                    filteredRegion.Add(region);
                }
            });

            return filteredRegion;
        }

        private bool pointInPolygon(Region region, decimal lat, decimal lng)
        {
            decimal[] polyX = region.Points.Select(a => a.Lat).ToArray();
            decimal[] polyY = region.Points.Select(a => a.Lng).ToArray();

            decimal x = lat;
            decimal y = lng;

            int polyCorners = region.Points.Count;
            int i, j = polyCorners - 1;
            bool oddNodes = false;

            for (i = 0; i < polyCorners; i++)
            {
                if (polyY[i] < y && polyY[j] >= y || polyY[j] < y && polyY[i] >= y)
                {
                    if (polyX[i] + (y - polyY[i]) / (polyY[j] - polyY[i]) * (polyX[j] - polyX[i]) < x)
                    {
                        oddNodes = !oddNodes;
                    }
                }
                j = i;
            }

            return oddNodes;
        }
        #endregion
    }
}