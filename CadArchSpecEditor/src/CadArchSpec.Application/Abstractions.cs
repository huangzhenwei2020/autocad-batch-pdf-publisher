using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Documents;
using CadArchSpec.Domain.Projects;
using CadArchSpec.Domain.Review;
using CadArchSpec.Domain.Rules;

namespace CadArchSpec.Application
{
    public interface IArchitectureProjectRepository
    {
        ArchitectureProject LoadProject(Guid projectId);
        void SaveProject(ArchitectureProject project);
        ArchitectureDesignSpecDocument LoadDocument(Guid documentId);
        void SaveDocument(ArchitectureDesignSpecDocument document);
    }

    public interface IRulePackageProvider
    {
        IReadOnlyList<RulePackage> LoadFor(string jurisdictionCode, DateTime submissionDate);
    }

    public interface IArchitectureReviewService
    {
        IReadOnlyList<ReviewIssue> Review(ArchitectureProject project, ArchitectureDesignSpecDocument document, IEnumerable<RulePackage> packages);
    }
}
