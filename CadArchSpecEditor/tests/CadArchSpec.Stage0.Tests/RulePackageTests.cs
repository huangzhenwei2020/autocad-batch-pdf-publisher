using System;
using System.IO;
using CadArchSpec.Domain.Common;
using CadArchSpec.RuleEngine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class RulePackageTests
    {
        [Fact]
        public void LoadsVersionedNationalSamplePackage()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "rules", "CN", "common", "package.json");
            var package = new RulePackageLoader().LoadFile(path);

            Assert.Equal(1, package.SchemaVersion);
            Assert.Equal("CN", package.JurisdictionCode);
            Assert.Equal("Draft", package.Status);
            Assert.Equal(9, package.Rules.Count);
            Assert.Equal(ReviewSeverity.Blocker, package.Rules[0].Severity);
        }

        [Fact]
        public void EvaluatesDraftFoundationPackageAsPreview()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "rules", "CN", "common", "package.json");
            var package = new RulePackageLoader().LoadFile(path);
            var workspace = JObject.Parse(@"{
              'submissionDate':'2026-07-30',
              'buildingType':'办公建筑',
              'fields':[
                {'path':'project.projectName','value':'项目','state':'confirmed'},
                {'path':'project.location','value':'南宁市','state':'pending'},
                {'path':'building.height','value':'36','state':'confirmed'},
                {'path':'fire.resistanceRating','value':'一级','state':'pending'}
              ],
              'sections':[
                {'id':'design-basis','enabled':true,'content':{'content':[{},{}]}},
                {'id':'project-overview','enabled':true,'content':{'content':[{},{}]}},
                {'id':'technical-indicators','enabled':true,'content':{'content':[{},{}]}},
                {'id':'accessibility','enabled':true,'content':{'content':[{},{}]}},
                {'id':'fire','enabled':true,'content':{'content':[{},{}]}}
              ]
            }");

            var result = new WorkspaceRuleEvaluator().Evaluate(package, workspace);

            Assert.Equal("Draft", result.PackageStatus);
            Assert.False(result.LocalRulesLoaded);
            Assert.Contains(result.Issues, issue => issue.RuleId == "CN-ARCH-FIELD-002");
            Assert.Contains(result.Issues, issue => issue.RuleId == "CN-ARCH-FIELD-004");
            Assert.DoesNotContain(result.Issues, issue => issue.RuleId == "CN-ARCH-FIELD-001");
        }

        [Fact]
        public void RejectsScriptLikeCheckType()
        {
            const string json = "{\"schemaVersion\":1,\"packageId\":\"BAD\",\"version\":\"1\",\"jurisdictionCode\":\"CN\",\"displayName\":\"bad\",\"status\":\"Draft\",\"signature\":\"\",\"rules\":[{\"ruleId\":\"BAD-001\",\"version\":1,\"title\":\"bad\",\"jurisdictionCode\":\"CN\",\"buildingTypes\":[\"common\"],\"severity\":\"error\",\"checkType\":\"executeScript\",\"target\":{},\"parameters\":{},\"message\":\"bad\",\"requiresProfessionalConfirmation\":false,\"references\":[]}]}";
            Assert.Throws<RulePackageException>(() => new RulePackageLoader().LoadJson(json));
        }
    }
}
