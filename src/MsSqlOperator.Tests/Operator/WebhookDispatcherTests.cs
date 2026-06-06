using System.Text.Json;
using FluentAssertions;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Operator;

public class WebhookDispatcherTests
{
    private static WebhookDispatcher BuildDispatcher()
    {
        var services = new ServiceCollection();
        services.AddScoped<SqlInstanceValidator>();
        var provider = services.BuildServiceProvider();
        var options = new OperatorOptions { Assemblies = [typeof(SqlInstance).Assembly] };
        return new WebhookDispatcher(provider, options);
    }

    private static JsonElement ToJsonElement(object obj)
        => JsonSerializer.Deserialize<JsonElement>(KubernetesJson.Serialize(obj));

    [Fact]
    public async Task ValidateAsync_CreateValidInstance_DeserializesAndAllows()
    {
        // Regression: the dispatcher resolved KubernetesJson.Deserialize<T> by reflection and
        // matched on a single string parameter. KubernetesClient's overload is
        // (string, JsonSerializerOptions?), so .First() threw "Sequence contains no matching
        // element" and every admission request was denied with HTTP 500.
        var dispatcher = BuildDispatcher();

        var instance = new SqlInstance
        {
            ApiVersion = "mssql.operator/v1alpha1",
            Kind = "SqlInstance",
            Metadata = new V1ObjectMeta { Name = "t", NamespaceProperty = "default" },
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2022", Edition = "Developer" }
        };

        var review = new AdmissionReview
        {
            Request = new AdmissionRequest
            {
                Uid = "req-1",
                Operation = "CREATE",
                Object = ToJsonElement(instance)
            }
        };

        var result = await dispatcher.ValidateAsync("sqlinstance", review, CancellationToken.None);

        result.Response.Should().NotBeNull();
        result.Response!.Uid.Should().Be("req-1");
        result.Response.Allowed.Should().BeTrue();
        (result.Response.Status?.Code).Should().NotBe(500);
    }

    [Fact]
    public async Task ValidateAsync_CreateInvalidInstance_DeniesWithoutServerError()
    {
        // An invalid spec must be a clean validation denial, not a 500 dispatcher error.
        var dispatcher = BuildDispatcher();

        var instance = new SqlInstance
        {
            ApiVersion = "mssql.operator/v1alpha1",
            Kind = "SqlInstance",
            Metadata = new V1ObjectMeta { Name = "t", NamespaceProperty = "default" },
            Spec = new SqlInstanceSpec { Mode = "NotAMode", Version = "2022", Edition = "Developer" }
        };

        var review = new AdmissionReview
        {
            Request = new AdmissionRequest
            {
                Uid = "req-2",
                Operation = "CREATE",
                Object = ToJsonElement(instance)
            }
        };

        var result = await dispatcher.ValidateAsync("sqlinstance", review, CancellationToken.None);

        result.Response.Should().NotBeNull();
        result.Response!.Allowed.Should().BeFalse();
        (result.Response.Status?.Code).Should().NotBe(500);
    }
}
