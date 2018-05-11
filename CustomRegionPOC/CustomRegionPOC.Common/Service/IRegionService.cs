using CustomRegionPOC.Common.Model;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace CustomRegionPOC.Common.Service
{
    public interface IRegionService
    {
        Task Create(Region region);

        Task<List<Region>> Get(decimal lat, decimal lng);
    }
}
