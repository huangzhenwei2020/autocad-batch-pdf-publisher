using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class PublishPlanStore
    {
        public static event System.Action FramesChanged;
        private const string FileName = "BatchPdfPublisher.frames.json";

        public List<FrameDefinition> LoadFrames()
        {
            var path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), FileName);
            if (!File.Exists(path)) return DefaultFrames();
            using (var stream = File.OpenRead(path))
                return (List<FrameDefinition>)new DataContractJsonSerializer(typeof(List<FrameDefinition>)).ReadObject(stream);
        }

        public void SaveFrames(List<FrameDefinition> frames)
        {
            var path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), FileName);
            using (var stream = File.Create(path))
                new DataContractJsonSerializer(typeof(List<FrameDefinition>)).WriteObject(stream, frames);
            FramesChanged?.Invoke();
        }

        private static List<FrameDefinition> DefaultFrames() => new List<FrameDefinition>();
    }
}
