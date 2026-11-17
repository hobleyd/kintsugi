using Kintsugi.Application.ScriptApproval;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Baselines;

/// <summary>Builds a new unsigned version: the one place a document is written, so its ids and its hash
/// can never disagree with the row that stores it.</summary>
public static class BaselineVersionFactory
{
    public static BaselineVersion Create(Baseline baseline, int number, IReadOnlyList<BaselineItem> items, string? createdBy)
    {
        var versionId = Guid.NewGuid();
        var document = BaselineDocument.ToCanonicalJson(
            new BaselineDocument(BaselineDocument.CurrentSchema, baseline.Id, baseline.Name, versionId, number, items));
        return BaselineVersion.Create(versionId, baseline.Id, number, document, ScriptContentHash.Of(document), createdBy);
    }
}
