using System.Linq;

namespace AgentStudio.Contracts;

/// <summary>One parameter's display description, keyed by the same Props dict key
/// NodePropertiesEditor's Get/Put helpers already use (e.g. "promptTemplate",
/// "resultVariable").</summary>
public sealed record NodeParamInfo(string Key, string Description);

/// <summary>What a node type does, plus a description for each of its editable parameters —
/// backs the tooltip in the graph editor's palette and the header/field hints in
/// NodePropertiesEditor. One entry per WorkflowNode subclass Type string; NodeCatalogTests
/// asserts every subclass has one, the same "can't add a node type without wiring it up"
/// discipline CLAUDE.md documents for the JSON converter and GraphMapper.</summary>
public sealed record NodeInfo(string Type, string Title, string Summary, NodeParamInfo[] Params)
{
    public string? ParamDescription(string key) => Array.Find(Params, p => p.Key == key)?.Description;
}

public static class NodeCatalog
{
    public static readonly IReadOnlyDictionary<string, NodeInfo> Nodes = new List<NodeInfo>
    {
        new("start", "Start", "Entry point of the workflow — every run begins here. No configurable properties.", []),

        new("prompt", "Prompt", "Sends a prompt to the agent's LLM and streams the reply back to the caller.", [
            new("promptTemplate", "The message sent to the LLM. Supports {input} and {variables.name} placeholders."),
            new("resultVariable", "Variable name the LLM's full reply is saved to."),
        ]),

        new("message", "Message", "Emits fixed (or templated) text to the output — no LLM call.", [
            new("text", "Text emitted as-is, after template expansion. Supports {input}/{variables.x}."),
        ]),

        new("condition", "Condition", "Branches the workflow by comparing two values. Requires exactly two outgoing edges: one 'true', one 'false'.", [
            new("left", "Left-hand value — a literal, {input}, or variables.x."),
            new("operator", "How left and right are compared."),
            new("right", "Right-hand value — a literal or variables.x."),
        ]),

        new("http", "HTTP Request", "Calls an external HTTP endpoint and saves the response body to a variable. Blocked from reaching loopback/private hosts by default (SSRF guard).", [
            new("method", "HTTP method for the request."),
            new("url", "Target URL. Supports {input}/{variables.x} placeholders."),
            new("body", "Optional JSON request body template."),
            new("timeoutSeconds", "How long to wait for a response before failing this step."),
            new("retries", "Number of automatic retries on failure (0 = no retry)."),
            new("resultVariable", "Variable name the response body is saved to."),
        ]),

        new("variable", "Set Variable", "Sets a variable to a fixed or templated value, for later steps to reference.", [
            new("name", "Name of the variable to set."),
            new("value", "Value to store — supports {input}/{variables.x} placeholders."),
        ]),

        new("documentSearch", "Document Search", "Searches this agent's uploaded documents (RAG) for chunks relevant to a query and saves the matched text to a variable. Requires the agent's provider to have an embedding model configured.", [
            new("query", "Search query — supports {input}/{variables.x} placeholders."),
            new("topK", "How many matching chunks to retrieve."),
            new("resultVariable", "Variable name the matched chunk text is saved to."),
        ]),

        new("subAgent", "Sub-Agent Call", "Calls another agent's published version as a one-shot step. Its own conversation — not shared history with this one.", [
            new("targetAgentId", "The agent to call — must have at least one published version."),
            new("inputTemplate", "Input text sent to the target agent — supports {input}/{variables.x}."),
            new("resultVariable", "Variable name the target agent's full reply is saved to."),
        ]),

        new("databaseQuery", "Database Query", "Runs a parameterized SQL query against a named database connection and saves the JSON result to a variable. Read-only connections only allow a single SELECT.", [
            new("connectionName", "Which configured database connection to query."),
            new("query", "SQL text with @name placeholders — never interpolate {variables.x} directly into the query itself, that's SQL injection."),
            new("parameters", "Named values bound to the @name placeholders — each supports {input}/{variables.x}."),
            new("timeoutSeconds", "How long to wait for the query before failing this step."),
            new("resultVariable", "Variable name the JSON result ({rows, rowCount, truncated}) is saved to."),
        ]),

        new("jsonParse", "Parse JSON", "Extracts one value out of a JSON blob by a dot/bracket path — e.g. an earlier http/databaseQuery/integrator result. Fails clearly if the input isn't valid JSON or the path doesn't exist.", [
            new("input", "The JSON text to read from — supports {input}/{variables.x}."),
            new("path", "Dot/bracket path into the JSON, e.g. data.items[0].name."),
            new("resultVariable", "Variable name the extracted value is saved to."),
        ]),

        new("expression", "Formula", "Computes one value with a small spreadsheet-like formula (arithmetic, comparisons, IF/CONCAT/etc.) and saves it to a variable.", [
            new("formula", "The formula to evaluate. {input}/{variables.x} resolve to typed values (number/bool/string) before evaluation."),
            new("resultVariable", "Variable name the computed value is saved to."),
        ]),

        new("collectionGet", "Collection Get", "Reads a key from this agent's persistent collection — a key/value store that survives across runs and conversations, unlike a normal variable.", [
            new("key", "Which collection key to read — supports {input}/{variables.x}."),
            new("defaultValue", "Value used when the key was never set."),
            new("resultVariable", "Variable name the read value is saved to."),
        ]),

        new("collectionSet", "Collection Set", "Writes a key to this agent's persistent collection.", [
            new("key", "Which collection key to write — supports {input}/{variables.x}."),
            new("value", "Value to store — supports {input}/{variables.x}."),
        ]),

        new("integrator", "Integrator", "Calls a registered custom integration (e.g. GitLab, Jira) and saves its result to a variable. New integrators are written as code, not configured here.", [
            new("integratorName", "Which registered integration to call."),
            new("config", "Named configuration values the integrator expects — each supports {input}/{variables.x}."),
            new("resultVariable", "Variable name the integrator's result is saved to."),
        ]),

        new("parallel", "Parallel Split", "Fans out to two or more branches that run concurrently. All branches must reconverge at a single Join node.", []),

        new("join", "Join", "Where a parallel split's branches reconverge, in the order they were connected. Continue the workflow from here.", []),

        new("end", "End", "Terminates this path through the workflow. A run can have multiple End nodes; whichever is reached first ends that path.", [
            new("outputTemplate", "Optional final text for this path — supports {input}/{variables.x}. Left blank, the path ends with whatever was last emitted."),
        ]),
    }.ToDictionary(n => n.Type);

    public static NodeInfo? Get(string type) => Nodes.GetValueOrDefault(type);
}
