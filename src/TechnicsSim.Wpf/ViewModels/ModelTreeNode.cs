using System.Collections.ObjectModel;
using TechnicsSim.LDraw.Geometry;

namespace TechnicsSim.Wpf.ViewModels;

/// <summary>
/// A node in the model tree. Leaves carry a logical instance ID, which is the same identifier
/// the renderer returns from a click, so tree selection and viewport selection are two views of
/// one identity rather than two parallel schemes that can disagree.
/// </summary>
public sealed class ModelTreeNode
{
    public ModelTreeNode(string label, string? instanceId = null, string? detail = null)
    {
        Label = label;
        InstanceId = instanceId;
        Detail = detail;
    }

    public string Label { get; }

    /// <summary>Non-null for leaves that correspond to a placed logical part.</summary>
    public string? InstanceId { get; }

    public string? Detail { get; }

    public ObservableCollection<ModelTreeNode> Children { get; } = [];

    public bool IsExpanded { get; set; }

    /// <summary>
    /// Builds the tree from a scene, grouping by the submodel path encoded in each instance ID.
    ///
    /// The ID is a chain of <c>section@line</c> segments, so the section names in it reconstruct
    /// the model's own submodel hierarchy without needing a second traversal.
    /// </summary>
    public static ModelTreeNode Build(RenderScene scene, string rootLabel)
    {
        var root = new ModelTreeNode(rootLabel) { IsExpanded = true };
        var groups = new Dictionary<string, ModelTreeNode> { [string.Empty] = root };

        foreach (var instance in scene.Instances)
        {
            var segments = instance.InstanceId.Split('|');
            var parent = root;
            var path = string.Empty;

            // Every segment except the last names a containing submodel reference.
            for (var i = 0; i < segments.Length - 1; i++)
            {
                path = path.Length == 0 ? segments[i] : $"{path}|{segments[i]}";

                if (!groups.TryGetValue(path, out var node))
                {
                    groups[path] = node = new ModelTreeNode(SubmodelLabel(segments, i));
                    parent.Children.Add(node);
                }

                parent = node;
            }

            parent.Children.Add(new ModelTreeNode(
                instance.PartName,
                instance.InstanceId,
                $"colour {instance.Colour.Code}"));
        }

        AnnotateCounts(root);
        return root;
    }

    /// <summary>
    /// Names a submodel group after the file the reference points into, which is the segment
    /// that follows it, falling back to the declaring section for the last hop.
    /// </summary>
    private static string SubmodelLabel(string[] segments, int index)
    {
        var next = segments[index + 1];
        var at = next.LastIndexOf('@');
        return at > 0 ? next[..at] : next;
    }

    private static int AnnotateCounts(ModelTreeNode node)
    {
        if (node.Children.Count == 0)
        {
            return 1;
        }

        var total = node.Children.Sum(AnnotateCounts);
        node.CountLabel = $"({total:N0})";
        return total;
    }

    /// <summary>Instance count beneath a group node, shown next to its label.</summary>
    public string? CountLabel { get; private set; }

    public override string ToString() => Label;
}
