{{/*
Object names are fixed to the Compose service names (ADR-0006), so these helpers only build
labels and image references. Each is called with (dict "root" $ "component" "<name>").
*/}}

{{- define "vehicle-diagnostics.chart" -}}
{{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "vehicle-diagnostics.selectorLabels" -}}
app.kubernetes.io/name: {{ .component }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end }}

{{- define "vehicle-diagnostics.labels" -}}
{{ include "vehicle-diagnostics.selectorLabels" . }}
app.kubernetes.io/part-of: vehicle-diagnostics
app.kubernetes.io/version: {{ .root.Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ include "vehicle-diagnostics.chart" .root }}
{{- end }}

{{/* "repository:tag" from an image block of values. */}}
{{- define "vehicle-diagnostics.image" -}}
{{ printf "%s:%s" .repository (toString .tag) }}
{{- end }}

{{/*
Init container that blocks until every "host:port" in .targets accepts TCP connections. A
Service only routes to ready pods, so an accepted connection means the dependency is ready.
*/}}
{{- define "vehicle-diagnostics.waitFor" -}}
- name: wait-for-dependencies
  image: {{ include "vehicle-diagnostics.image" .root.Values.busybox.image }}
  imagePullPolicy: {{ .root.Values.busybox.image.pullPolicy }}
  command:
    - sh
    - -c
    - |
      for target in {{ join " " .targets }}; do
        until nc -z -w 2 "${target%:*}" "${target#*:}"; do
          echo "waiting for $target"
          sleep 2
        done
        echo "$target accepts connections"
      done
  resources:
    {{- toYaml .root.Values.busybox.resources | nindent 4 }}
{{- end }}
