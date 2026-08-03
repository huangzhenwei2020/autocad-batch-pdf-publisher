using System;
using System.Collections.Generic;
using System.Linq;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Standards;

namespace CadArchSpec.StandardRegistry
{
    public interface IStandardRegistry
    {
        StandardReference Find(string code);
        IReadOnlyList<StandardReference> ApplicableOn(DateTime date, string jurisdictionCode);
        void Replace(IEnumerable<StandardReference> standards);
    }

    public sealed class InMemoryStandardRegistry : IStandardRegistry
    {
        private readonly List<StandardReference> _standards = new List<StandardReference>();

        public StandardReference Find(string code)
        {
            return _standards.FirstOrDefault(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<StandardReference> ApplicableOn(DateTime date, string jurisdictionCode)
        {
            return _standards
                .Where(item => string.Equals(item.JurisdictionCode, jurisdictionCode, StringComparison.OrdinalIgnoreCase))
                .Where(item => item.Status == StandardStatus.Active || item.Status == StandardStatus.PartiallySuperseded)
                .Where(item => !item.EffectiveDate.HasValue || item.EffectiveDate.Value.Date <= date.Date)
                .Where(item => !item.RepealedDate.HasValue || item.RepealedDate.Value.Date > date.Date)
                .ToList();
        }

        public void Replace(IEnumerable<StandardReference> standards)
        {
            _standards.Clear();
            if (standards != null) _standards.AddRange(standards.Where(item => item != null));
        }
    }
}
