namespace AgentStudio.Domain;

public sealed class WorkflowValidator
{
    public static List<string> Validate(WorkflowGraph graph)
    {
        var errors = new List<string>();

        var startCount = graph.Nodes.Count(n => n is StartNode);
        if (startCount != 1)
            errors.Add($"Workflow must contain exactly one Start node (found {startCount}).");

        if (!graph.Nodes.Any(n => n is EndNode))
            errors.Add("Workflow must contain at least one End node.");

        var ids = graph.Nodes.Select(n => n.Id).ToList();
        if (ids.Distinct().Count() != ids.Count)
            errors.Add("Node identifiers must be unique.");

        var nodeIds = ids.ToHashSet();
        foreach (var edge in graph.Edges)
        {
            if (!nodeIds.Contains(edge.SourceNodeId))
                errors.Add($"Edge {edge.Id} references missing source node {edge.SourceNodeId}.");
            if (!nodeIds.Contains(edge.TargetNodeId))
                errors.Add($"Edge {edge.Id} references missing target node {edge.TargetNodeId}.");
        }

        foreach (var node in graph.Nodes.OfType<ConditionNode>())
        {
            var branches = graph.Edges.Where(e => e.SourceNodeId == node.Id).Select(e => e.Branch).ToList();
            if (!branches.Contains("true") || !branches.Contains("false"))
                errors.Add($"Condition node {node.Id} must have both 'true' and 'false' outgoing edges.");
        }

        foreach (var node in graph.Nodes.OfType<ApprovalNode>())
        {
            var branches = graph.Edges.Where(e => e.SourceNodeId == node.Id).Select(e => e.Branch).ToList();
            if (!branches.Contains("approved") || !branches.Contains("rejected"))
                errors.Add($"Approval node {node.Id} must have both 'approved' and 'rejected' outgoing edges.");
        }

        errors.AddRange(ValidateParallelRegions(graph));

        // reachability from start
        var start = graph.Nodes.OfType<StartNode>().FirstOrDefault();
        if (start is not null)
        {
            var reachable = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(start.Id);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!reachable.Add(current)) continue;
                foreach (var edge in graph.Edges.Where(e => e.SourceNodeId == current))
                    queue.Enqueue(edge.TargetNodeId);
            }

            foreach (var node in graph.Nodes)
                if (!reachable.Contains(node.Id))
                    errors.Add($"Node {node.Id} ({node.Type}) is not reachable from Start.");

            // Cycles are allowed since phase 2 (loops). The per-version MaxSteps cap in
            // WorkflowRunner is the safety net against runaway/infinite loops.
        }

        return errors;
    }

    /// <summary>
    /// Each ParallelNode must fan out to ≥2 unconditional branches that all reconverge at exactly
    /// one shared JoinNode, with no other path bypassing the split to reach that same join, and
    /// no nested parallel region inside a branch (an explicit scope cut — see PLAN.md faza 2).
    /// A branch may itself contain internal branching (e.g. a Condition) as long as every path it
    /// produces converges on that one join.
    /// </summary>
    private static List<string> ValidateParallelRegions(WorkflowGraph graph)
    {
        var errors = new List<string>();
        var nodesById = graph.Nodes.ToDictionary(n => n.Id);

        foreach (var split in graph.Nodes.OfType<ParallelNode>())
        {
            var branchEdges = graph.Edges.Where(e => e.SourceNodeId == split.Id).ToList();
            if (branchEdges.Count < 2)
            {
                errors.Add($"Parallel node {split.Id} must have at least 2 outgoing edges (found {branchEdges.Count}).");
                continue;
            }
            if (branchEdges.Any(e => e.Branch is not null))
                errors.Add($"Parallel node {split.Id} outgoing edges must be unconditional (no true/false branch).");

            var reachableFromSplit = new HashSet<string>();
            var boundaries = new HashSet<WorkflowNode>();
            var nestedParallel = false;

            foreach (var edge in branchEdges)
            {
                var visited = new HashSet<string>();
                var queue = new Queue<string>();
                queue.Enqueue(edge.TargetNodeId);
                while (queue.Count > 0)
                {
                    var id = queue.Dequeue();
                    if (!visited.Add(id)) continue;
                    reachableFromSplit.Add(id);

                    if (!nodesById.TryGetValue(id, out var node)) continue; // dangling edge already reported

                    if (node is JoinNode)
                    {
                        boundaries.Add(node);
                        continue; // don't walk past a join
                    }
                    if (node is ParallelNode nested && nested.Id != split.Id)
                    {
                        nestedParallel = true;
                        boundaries.Add(node);
                        continue;
                    }

                    var outgoing = graph.Edges.Where(e => e.SourceNodeId == id).ToList();
                    if (outgoing.Count == 0)
                    {
                        boundaries.Add(node); // dead end — wrong-type boundary, reported below
                        continue;
                    }
                    foreach (var next in outgoing)
                        queue.Enqueue(next.TargetNodeId);
                }
            }

            if (nestedParallel)
                errors.Add($"Parallel node {split.Id}: nested parallel regions are not supported.");

            var joins = boundaries.OfType<JoinNode>().ToList();
            var nonJoinBoundaries = boundaries.Where(b => b is not JoinNode).ToList();

            if (joins.Count == 0)
                errors.Add($"Parallel node {split.Id}: no branch reaches a Join node.");
            else if (joins.Count > 1)
                errors.Add($"Parallel node {split.Id}: branches converge to different Join nodes ({string.Join(", ", joins.Select(j => j.Id))}).");

            foreach (var b in nonJoinBoundaries)
                errors.Add($"Parallel node {split.Id}: a branch ends at {b.Type} '{b.Id}' instead of a Join node.");

            if (joins.Count == 1)
            {
                var join = joins[0];
                var bypassing = graph.Edges
                    .Where(e => e.TargetNodeId == join.Id && !reachableFromSplit.Contains(e.SourceNodeId))
                    .ToList();
                foreach (var e in bypassing)
                    errors.Add($"Parallel node {split.Id}: Join node {join.Id} is also reachable via edge {e.Id}, bypassing the split.");
            }
        }

        return errors;
    }
}
