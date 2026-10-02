{{/*
Expand the name of the chart.
*/}}
{{- define "mssql-operator.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "mssql-operator.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "mssql-operator.labels" -}}
helm.sh/chart: {{ include "mssql-operator.name" . }}-{{ .Chart.Version | replace "+" "_" }}
{{ include "mssql-operator.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "mssql-operator.selectorLabels" -}}
app.kubernetes.io/name: {{ include "mssql-operator.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "mssql-operator.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "mssql-operator.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
The API server addresses as a JSON list of CIDRs — networkPolicy.apiServerCidrs, else
the endpoints of the default/kubernetes Service. Empty when the chart cannot read the cluster (helm
template, ArgoCD render): the callers then fall back to an open rule.
*/}}
{{- define "mssql-operator.apiServerCidrs" -}}
{{- $cidrs := list }}
{{- range .Values.networkPolicy.apiServerCidrs }}
{{- $cidrs = append $cidrs . }}
{{- end }}
{{- if not $cidrs }}
{{- $slice := lookup "discovery.k8s.io/v1" "EndpointSlice" "default" "kubernetes" }}
{{- range (default (list) (and $slice $slice.endpoints)) }}
{{- range (default (list) .addresses) }}
{{- $cidrs = append $cidrs (printf "%s/%s" . (ternary "128" "32" (contains ":" .))) }}
{{- end }}
{{- end }}
{{- end }}
{{- toJson $cidrs }}
{{- end }}

{{/*
Egress rule to the API server (443/6443): restricted to its addresses when they are known. The
rule used to open both ports to every destination, so allowExternalHttps=false changed nothing.
*/}}
{{- define "mssql-operator.apiServerEgress" -}}
{{- $cidrs := include "mssql-operator.apiServerCidrs" . | fromJsonArray }}
# Kubernetes API server{{ if not $cidrs }} (addresses unknown at render time: any destination){{ end }}
- ports:
    - port: 443
      protocol: TCP
    - port: 6443
      protocol: TCP
  {{- if $cidrs }}
  to:
    {{- range $cidrs }}
    - ipBlock:
        cidr: {{ . }}
    {{- end }}
  {{- end }}
{{- end }}
