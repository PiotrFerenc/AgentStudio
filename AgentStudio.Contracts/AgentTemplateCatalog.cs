using System.Linq;

namespace AgentStudio.Contracts;

/// <summary>A complete starter agent configuration — not a fragment to paste into an existing
/// graph (that's GraphComponent), a whole agent's initial Graph/SystemInstructions/FormFields.
/// Code-defined (like NodeCatalog), not a database table — a curated, fixed set shipped with
/// the app, not something an admin creates at runtime.</summary>
public sealed record AgentTemplate(
    string Id,
    string Name,
    string Description,
    string SystemInstructions,
    WorkflowGraphDto Graph,
    List<Domain.FormField>? FormFields = null);

public static class AgentTemplateCatalog
{
    public static readonly IReadOnlyList<AgentTemplate> Templates = new List<AgentTemplate>
    {
        new(
            Id: "rag-qa",
            Name: "RAG Q&A",
            Description: "Answers questions from this agent's uploaded documents. Needs an embedding model set on the provider (Providers page) and documents uploaded after creation (Documents tab).",
            SystemInstructions: "Answer questions using only the provided document context. If the answer isn't in the context, say you don't know rather than guessing.",
            Graph: new WorkflowGraphDto
            {
                Nodes = new List<WorkflowNodeDto>
                {
                    new() { Id = "start", Type = "start", Label = "Start", X = 0, Y = 200 },
                    new() { Id = "search1", Type = "documentSearch", Label = "Search documents", X = 300, Y = 200, Props = new()
                    {
                        ["query"] = "{input}",
                        ["topK"] = 3,
                        ["resultVariable"] = "context"
                    } },
                    new() { Id = "prompt1", Type = "prompt", Label = "Answer", X = 600, Y = 200, Props = new()
                    {
                        ["promptTemplate"] = "Answer using this context only:\n{variables.context}\n\nQuestion: {input}",
                        ["resultVariable"] = "llmResult"
                    } },
                    new() { Id = "end", Type = "end", Label = "End", X = 900, Y = 200, Props = new() { ["outputTemplate"] = "{variables.llmResult}" } },
                },
                Edges = new List<WorkflowEdgeDto>
                {
                    new() { Id = "e1", SourceNodeId = "start", TargetNodeId = "search1" },
                    new() { Id = "e2", SourceNodeId = "search1", TargetNodeId = "prompt1" },
                    new() { Id = "e3", SourceNodeId = "prompt1", TargetNodeId = "end" },
                }
            }),

        new(
            Id: "webhook-integrator",
            Name: "Webhook → Integrator",
            Description: "Fires a custom integration (GitLab, Jira, ...) from an external system's webhook payload — trigger it at /api/agents/{id}/versions/{version}/webhook, not chat. Payload keys become {variables.x}; pick the integrator and fill in Config on the integrator node after creation.",
            SystemInstructions: "",
            Graph: new WorkflowGraphDto
            {
                Nodes = new List<WorkflowNodeDto>
                {
                    new() { Id = "start", Type = "start", Label = "Start", X = 0, Y = 200 },
                    new() { Id = "integrator1", Type = "integrator", Label = "Call integrator", X = 300, Y = 200, Props = new()
                    {
                        ["integratorName"] = "",
                        ["config"] = new Dictionary<string, string> { ["title"] = "{variables.title}" },
                        ["resultVariable"] = "integratorResult"
                    } },
                    new() { Id = "end", Type = "end", Label = "End", X = 600, Y = 200, Props = new() { ["outputTemplate"] = "Done: {variables.integratorResult}" } },
                },
                Edges = new List<WorkflowEdgeDto>
                {
                    new() { Id = "e1", SourceNodeId = "start", TargetNodeId = "integrator1" },
                    new() { Id = "e2", SourceNodeId = "integrator1", TargetNodeId = "end" },
                }
            }),

        new(
            Id: "validated-form",
            Name: "Formularz z walidacją",
            Description: "A public form (/run/{id}/{version}) with a required name and a validated email field — demonstrates FormField validation end to end. Run through the Form tab to add more fields.",
            SystemInstructions: "",
            Graph: new WorkflowGraphDto
            {
                Nodes = new List<WorkflowNodeDto>
                {
                    new() { Id = "start", Type = "start", Label = "Start", X = 0, Y = 200 },
                    new() { Id = "prompt1", Type = "prompt", Label = "Acknowledge", X = 300, Y = 200, Props = new()
                    {
                        ["promptTemplate"] = "A new form submission arrived:\nName: {variables.name}\nEmail: {variables.email}\n\nWrite a short acknowledgement message.",
                        ["resultVariable"] = "llmResult"
                    } },
                    new() { Id = "end", Type = "end", Label = "End", X = 600, Y = 200, Props = new() { ["outputTemplate"] = "{variables.llmResult}" } },
                },
                Edges = new List<WorkflowEdgeDto>
                {
                    new() { Id = "e1", SourceNodeId = "start", TargetNodeId = "prompt1" },
                    new() { Id = "e2", SourceNodeId = "prompt1", TargetNodeId = "end" },
                }
            },
            FormFields: new List<Domain.FormField>
            {
                new() { Name = "name", Label = "Full name", Type = "text", Required = true, MinLength = 2 },
                new() { Name = "email", Label = "Email", Type = "email", Required = true, Pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$", ErrorMessage = "Enter a valid email address." },
            }),
    };

    public static AgentTemplate? Get(string? id) => id is null ? null : Templates.FirstOrDefault(t => t.Id == id);
}
