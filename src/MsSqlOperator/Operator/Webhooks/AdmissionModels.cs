using System.Text.Json;

namespace MsSqlOperator.Operator.Webhooks;

// admission.k8s.io/v1 AdmissionReview DTOs. The official k8s client does not ship these
// (they are API-server machinery), so we model the subset we need. Serialized with
// KubernetesJson (camelCase), matching what the API server sends and expects.

public sealed class AdmissionReview
{
    public string ApiVersion { get; set; } = "admission.k8s.io/v1";

    public string Kind { get; set; } = "AdmissionReview";

    public AdmissionRequest? Request { get; set; }

    public AdmissionResponse? Response { get; set; }
}

public sealed class AdmissionRequest
{
    public string Uid { get; set; } = string.Empty;

    /// <summary>CREATE, UPDATE or DELETE.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>The incoming object (raw JSON), absent on DELETE.</summary>
    public JsonElement? Object { get; set; }

    /// <summary>The previous object (raw JSON), present on UPDATE/DELETE.</summary>
    public JsonElement? OldObject { get; set; }

    public bool? DryRun { get; set; }
}

public sealed class AdmissionResponse
{
    public string Uid { get; set; } = string.Empty;

    public bool Allowed { get; set; }

    public string[]? Warnings { get; set; }

    public AdmissionStatus? Status { get; set; }
}

public sealed class AdmissionStatus
{
    public int Code { get; set; }

    public string? Message { get; set; }
}
