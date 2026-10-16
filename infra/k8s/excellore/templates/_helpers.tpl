{{- define "excellore.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "excellore.fullname" -}}
{{- default (include "excellore.name" .) .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "excellore.labels" -}}
app.kubernetes.io/part-of: {{ include "excellore.name" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end }}

{{- define "excellore.selector" -}}
app.kubernetes.io/part-of: {{ include "excellore.name" . }}
app.kubernetes.io/name: {{ .component }}
{{- end }}

{{- define "excellore.image" -}}
{{- $image := index .root.Values.images .key -}}
{{- if $image.digest -}}
{{ printf "%s@%s" $image.repository $image.digest }}
{{- else -}}
{{ printf "%s:%s" $image.repository $image.tag }}
{{- end -}}
{{- end }}

{{- define "excellore.podSecurityContext" -}}
runAsNonRoot: true
seccompProfile:
  type: RuntimeDefault
{{- end }}

{{- define "excellore.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: ["ALL"]
{{- end }}
