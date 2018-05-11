using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CustomRegionPOC.Common.Model;
using CustomRegionPOC.Common.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CustomRegionPOC.API.Controllers
{
    [Produces("application/json")]
    [Route("api/CustomRegion")]
    public class CustomRegionController : Controller
    {
        private IRegionService service;

        public CustomRegionController(IRegionService service)
        {
            this.service = service;
        }

        [HttpGet("{lat}/{lng}", Name = "Get")]
        public async Task<List<Region>> Get(decimal lat, decimal lng)
        {
            return await this.service.Get(lat, lng);
        }

        [HttpPost]
        public Task Post([FromBody]Region region)
        {
            return this.service.Create(region);
        }
    }
}
