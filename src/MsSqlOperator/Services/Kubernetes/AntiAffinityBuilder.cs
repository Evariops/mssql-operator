using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Kubernetes;

public class AntiAffinityBuilder : IAntiAffinityBuilder
{
    public V1Affinity? Build(AntiAffinitySpec? spec, Dictionary<string, string> selfLabels)
    {
        if (spec == null) return null;

        var required = new List<V1PodAffinityTerm>();
        var preferred = new List<V1WeightedPodAffinityTerm>();

        if (spec.RequiredSelfAntiAffinity != null)
        {
            required.Add(new V1PodAffinityTerm
            {
                LabelSelector = new V1LabelSelector { MatchLabels = selfLabels },
                TopologyKey = spec.RequiredSelfAntiAffinity.TopologyKey
            });
        }

        if (spec.PreferredSelfAntiAffinity != null)
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

        if (spec.RequiredAntiAffinityWith != null)
        {
            foreach (var rule in spec.RequiredAntiAffinityWith)
            {
                required.Add(new V1PodAffinityTerm
                {
                    LabelSelector = new V1LabelSelector
                    {
                        MatchLabels = new Dictionary<string, string>
                        {
                            { "mssql.operator/instance", rule.InstanceName }
                        }
                    },
                    TopologyKey = rule.TopologyKey
                });
            }
        }

        if (spec.PreferredAntiAffinityWith != null)
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
                            MatchLabels = new Dictionary<string, string>
                            {
                                { "mssql.operator/instance", rule.InstanceName }
                            }
                        },
                        TopologyKey = rule.TopologyKey
                    }
                });
            }
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
