using System;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Projects;
using CadArchSpec.RuleEngine;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class ProjectValueStateTests
    {
        private readonly ProjectValueStateValidator _validator = new ProjectValueStateValidator();

        [Fact]
        public void UnknownAndPendingValuesCannotDriveDeterministicReview()
        {
            Assert.False(_validator.CanBeUsedForDeterministicReview(new ProjectValue<decimal> { State = ValueState.Unknown }));
            Assert.False(_validator.CanBeUsedForDeterministicReview(new ProjectValue<decimal> { State = ValueState.Pending }));
        }

        [Fact]
        public void ConfirmRequiresTraceableSource()
        {
            var value = new ProjectValue<decimal>();
            Assert.Throws<ArgumentException>(() => _validator.Confirm(value, 49.8m, "", "测试用户", DateTimeOffset.Now));
        }

        [Fact]
        public void ConfirmedAndOverriddenValuesPreserveAuditInformation()
        {
            var value = new ProjectValue<decimal>();
            var time = new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.FromHours(8));
            _validator.Confirm(value, 49.8m, "立面图", "建筑师", time);
            Assert.True(_validator.CanBeUsedForDeterministicReview(value));
            Assert.Equal("立面图", value.Source);

            _validator.Override(value, 50.1m, "根据已批准变更单调整", "建筑师", time.AddMinutes(10));
            Assert.True(value.IsManuallyOverridden);
            Assert.Equal(ValueState.Overridden, value.State);
            Assert.Equal("根据已批准变更单调整", value.OverrideReason);
        }
    }
}
