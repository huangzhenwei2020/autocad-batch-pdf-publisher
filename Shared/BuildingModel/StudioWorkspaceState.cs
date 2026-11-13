namespace BatchPdfPublisher.BuildingModel
{
    // The editor owns model data, history and save decisions in both hosts.
    public sealed class StudioWorkspaceState
    {
        public bool Success { get; set; }
        public bool Ready { get; set; }
        public bool Dirty { get; set; }
        public long Window { get; set; }
        public string ModelPath { get; set; }
        public string Message { get; set; }
    }
}
