using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator;

namespace MsSqlOperator.Services.Kubernetes;

public class AntiAffinityBuilder : IAntiAffinityBuilder
{
    private const string HostnameKey = "kubernetes.io/hostname";

    public V1Affinity? Build(
        AntiAffinitySpec? spec, Dictionary<string, string> selfLabels,
        string? pool = null, string? poolAntiAffinity = null)
    {
        var required = new List<V1PodAffinityTerm>();
        var preferred = new List<V1WeightedPodAffinityTerm>();

        if (spec?.RequiredSelfAntiAffinity != null)
        {
            required.Add(new V1PodAffinityTerm
            {
                LabelSelector = new V1LabelSelector { MatchLabels = selfLabels },
                TopologyKey = spec.RequiredSelfAntiAffinity.TopologyKey
            });
        }

        if (spec?.PreferredSelfAntiAffinity != null)
        {
            preferred.Add(new V1WeightedPodAffinityTerm
            {
                Weight = spec.PreferredSelfAntiAffinity.Weight,
                PodAffinityTerm = new V1PodAffinityTerm
                {
                    LabelSelector = new V1LabelSelector { MatchLabels = selfLabels },
                    TopologyKey = spec.PreferredSelfAntiAffinity.TopologyKey
                }
            });
        }

        if (spec?.RequiredAntiAffinityWith != null)
        {
            foreach (var rule in spec.RequiredAntiAffinityWith)
            {
                required.Add(new V1PodAffinityTerm
                {
                    LabelSelector = new V1LabelSelector
                    {
                        MatchLabels = new Dictionary<string, string> { { OperatorLabels.Instance, rule.InstanceName } }
                    },
                    TopologyKey = rule.TopologyKey
                });
            }
        }

        if (spec?.PreferredAntiAffinityWith != null)
        {
            foreach (var rule in spec.PreferredAntiAffinityWith)
            {
                preferred.Add(new V1WeightedPodAffinityTerm
                {
                    Weight = rule.Weight,
                    PodAffinityTerm = new V1PodAffinityTerm
                    {
                        LabelSelector = new V1LabelSelector
                        {
                            MatchLabels = new Dictionary<string, string> { { OperatorLabels.Instance, rule.InstanceName } }
                        },
                        TopologyKey = rule.TopologyKey
                    }
                });
            }
        }

        // Pool anti-affinity: strict hostname separation between pool members.
        if (!string.IsNullOrEmpty(pool) && poolAntiAffinity != null)
        {
            var poolTerm = new V1PodAffinityTerm
            {
                LabelSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { { OperatorLabels.Pool, pool } }
                },
                TopologyKey = HostnameKey
            };

            if (poolAntiAffinity.Equals("Required", StringComparison.OrdinalIgnoreCase))
                required.Add(poolTerm);
            else
                preferred.Add(new V1WeightedPodAffinityTerm { Weight = 100, PodAffinityTerm = poolTerm });
        }

        if (required.Count == 0 && preferred.Count == 0) return null;

        return new V1Affinity
        {
            PodAntiAffinity = new V1PodAntiAffinity
            {
                RequiredDuringSchedulingIgnoredDuringExecution = required.Count > 0 ? required : null,
                PreferredDuringSchedulingIgnoredDuringExecution = preferred.Count > 0 ? preferred : null
            }
        };
    }
}
