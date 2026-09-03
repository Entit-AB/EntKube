{{- define "entkube-telemetry.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The standard Helm fullname: the release name, prefixed with the chart name only when it does not already
contain it. Without that collapse, the natural release name for this chart — "entkube-telemetry" — produces
"entkube-telemetry-entkube-telemetry-indexer", the prefix doubled. Every object in the release carries that
name, so anything outside the release that has to address one (the querier's indexerUrl, the collector's
OTLP exporter, EntKube's own catalog defaults) has to reproduce the doubling exactly or resolve nothing.

EntKube derives these names in C# too — EntKubeTelemetryService.Fullname mirrors this rule, and both are
pinned by a test that renders the chart. Changing the rule here without changing it there points the
management plane at hostnames that do not exist.
*/}}
{{- define "entkube-telemetry.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := include "entkube-telemetry.name" . -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "entkube-telemetry.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/name: {{ include "entkube-telemetry.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: entkube
{{- end -}}

{{- define "entkube-telemetry.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "entkube-telemetry.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "entkube-telemetry.secretName" -}}
{{- default (printf "%s-config" (include "entkube-telemetry.fullname" .)) .Values.node.existingSecret -}}
{{- end -}}

{{- define "entkube-telemetry.indexerName" -}}
{{- printf "%s-indexer" (include "entkube-telemetry.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "entkube-telemetry.querierName" -}}
{{- printf "%s-querier" (include "entkube-telemetry.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Non-secret configuration, identical for both roles so a value learned in one place applies to the other.
Identity and tokens come from the Secret; only tunables are here.

Every integer goes through `int64`. Helm parses YAML numbers as float64, and Go renders anything past
about a million in scientific notation — so a plain `{{ .Values… | quote }}` emits "1e+06" for 1000000
and "8.589934592e+09" for 8 GiB. .NET's Int64 converter rejects both, and the pod dies at startup with a
configuration-binding error that says nothing about Helm. Small values happen to render fine, which is
exactly why this is easy to miss until someone raises a limit.
*/}}
{{- define "entkube-telemetry.env" -}}
- name: Telemetry__DataPath
  value: /data/telemetry
- name: Telemetry__RetentionDays
  value: {{ .Values.telemetry.retentionDays | int64 | quote }}
- name: Telemetry__WarmMaxBytes
  value: {{ .Values.telemetry.warmMaxBytes | int64 | quote }}
- name: Telemetry__TieredLogRetention
  value: {{ .Values.telemetry.tieredLogRetention | quote }}
- name: Telemetry__VerboseLogRetentionDays
  value: {{ .Values.telemetry.verboseLogRetentionDays | int64 | quote }}
- name: Telemetry__RawSpanRetentionDays
  value: {{ .Values.telemetry.rawSpanRetentionDays | int64 | quote }}
- name: Telemetry__TraceSampleRatePercent
  value: {{ .Values.telemetry.traceSampleRatePercent | int64 | quote }}
- name: Telemetry__TraceKeepMinDurationMs
  value: {{ .Values.telemetry.traceKeepMinDurationMs | quote }}
- name: Telemetry__SegmentMaxDocs
  value: {{ .Values.telemetry.segmentMaxDocs | int64 | quote }}
- name: Telemetry__SegmentMaxAgeMinutes
  value: {{ .Values.telemetry.segmentMaxAgeMinutes | int64 | quote }}
- name: Telemetry__ArchiveZstdLevel
  value: {{ .Values.telemetry.archiveZstdLevel | int64 | quote }}
{{- if .Values.objectStorage.bucket }}
- name: Telemetry__ObjectStorage__Bucket
  value: {{ .Values.objectStorage.bucket | quote }}
- name: Telemetry__ObjectStorage__Endpoint
  value: {{ .Values.objectStorage.endpoint | quote }}
- name: Telemetry__ObjectStorage__Region
  value: {{ .Values.objectStorage.region | quote }}
- name: Telemetry__ObjectStorage__ForcePathStyle
  value: {{ .Values.objectStorage.forcePathStyle | quote }}
{{- end }}
- name: Node__TenantId
  valueFrom:
    secretKeyRef: { name: {{ include "entkube-telemetry.secretName" . }}, key: Node__TenantId }
- name: Node__ClusterId
  valueFrom:
    secretKeyRef: { name: {{ include "entkube-telemetry.secretName" . }}, key: Node__ClusterId }
- name: Node__QueryToken
  valueFrom:
    secretKeyRef: { name: {{ include "entkube-telemetry.secretName" . }}, key: Node__QueryToken }
{{- if .Values.objectStorage.bucket }}
- name: Telemetry__ObjectStorage__AccessKey
  valueFrom:
    secretKeyRef: { name: {{ include "entkube-telemetry.secretName" . }}, key: Telemetry__ObjectStorage__AccessKey }
- name: Telemetry__ObjectStorage__SecretKey
  valueFrom:
    secretKeyRef: { name: {{ include "entkube-telemetry.secretName" . }}, key: Telemetry__ObjectStorage__SecretKey }
{{- end }}
{{- end -}}

{{/*
The node writes only to its data volume, so the root filesystem can stay read-only — but .NET still needs
somewhere to put temp files, and the segment sealer stages archives before upload.
*/}}
{{- define "entkube-telemetry.tmpVolumeMounts" -}}
- name: tmp
  mountPath: /tmp
{{- end -}}
