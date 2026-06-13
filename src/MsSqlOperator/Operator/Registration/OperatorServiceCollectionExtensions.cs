using System.Reflection;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Runtime;
using MsSqlOperator.Operator.Webhooks;

namespace MsSqlOperator.Operator.Registration;

/// <summary>
/// Registration entry point, replacing KubeOps' <c>AddKubernetesOperator().RegisterComponents()</c>.
/// Builds the k8s client, scans for controllers/finalizers/validators, wires a reconciliation
/// runtime per entity type, and registers the hosted service.
/// </summary>
public static class OperatorServiceCollectionExtensions
{
    public static IServiceCollection AddOperator(this IServiceCollection services, Action<OperatorOptions>? configure = null)
    {
        var options = new OperatorOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<IKubernetes>(_ =>
        {
            var config = KubernetesClientConfiguration.IsInCluster()
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildConfigFromConfigFile();
            return new Kubernetes(config);
        });
        services.AddSingleton<IKubernetesClient, KubernetesClientAdapter>();
        services.AddSingleton<WebhookDispatcher>();

        var entityTypes = new HashSet<Type>();

        foreach (var asm in options.Assemblies)
        {
            foreach (var (impl, iface, entity) in ClosedImplementations(asm, typeof(IEntityController<>)))
            {
                services.AddScoped(iface, impl);
                entityTypes.Add(entity);
            }

            foreach (var (impl, iface, _) in ClosedImplementations(asm, typeof(IEntityFinalizer<>)))
                services.AddScoped(iface, impl);

            foreach (var validator in asm.GetTypes()
                         .Where(t => t is { IsAbstract: false, IsInterface: false }
                                     && t.GetCustomAttribute<ValidationWebhookAttribute>() is not null))
                services.AddScoped(validator);
        }

        // One reconciliation runtime per entity type that has a controller.
        foreach (var entity in entityTypes)
        {
            var runtimeType = typeof(EntityRuntime<>).MakeGenericType(entity);
            var kind = entity.Name;
            services.AddSingleton(typeof(IEntityRuntime), sp =>
            {
                var opts = sp.GetRequiredService<OperatorOptions>();
                return ActivatorUtilities.CreateInstance(sp, runtimeType,
                    opts.WorkerCount, opts.ResyncFor(kind) ?? TimeSpan.Zero);
            });
        }

        // The owned-resource watch multiplexer runs under the same leader guard as the
        // runtimes (started by ReconcilerHostedService), routing child events back to parents.
        services.AddSingleton<OwnedResourceWatcher>();

        services.AddHostedService<ReconcilerHostedService>();
        return services;
    }

    private static IEnumerable<(Type Impl, Type Interface, Type EntityType)> ClosedImplementations(Assembly asm, Type openInterface)
    {
        foreach (var type in asm.GetTypes())
        {
            if (type is not { IsAbstract: false, IsInterface: false })
                continue;
            foreach (var i in type.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == openInterface)
                    yield return (type, i, i.GetGenericArguments()[0]);
            }
        }
    }
}
