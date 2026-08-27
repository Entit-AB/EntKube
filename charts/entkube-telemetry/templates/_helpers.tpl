{{- define "entkube-telemetry.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "entkube-telemetry.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "entkube-telemetry.name" .) | trunc 63 | trimSuffix "-" -}}
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
*/}}
{{- define "entkube-telemetry.env" -}}
- name: Telemetry__DataPath
  value: /data/telemetry
- name: Telemetry__RetentionDays
  value: {{ .Values.telemetry.retentionDays | quote }}
- name: Telemetry__WarmMaxBytes
  value: {{ .Values.telemetry.warmMaxBytes | quote }}
- name: Telemetry__TieredLogRetention
  value: {{ .Values.telemetry.tieredLogRetention | quote }}
- name: Telemetry__VerboseLogRetentionDays
  value: {{ .Values.telemetry.verboseLogRetentionDays | quote }}
- name: Telemetry__RawSpanRetentionDays
  value: {{ .Values.telemetry.rawSpanRetentionDays | quote }}
- name: Telemetry__TraceSampleRatePercent
  value: {{ .Values.telemetry.traceSampleRatePercent | quote }}
- name: Telemetry__TraceKeepMinDurationMs
  value: {{ .Values.telemetry.traceKeepMinDurationMs | quote }}
- name: Telemetry__SegmentMaxDocs
  value: {{ .Values.telemetry.segmentMaxDocs | quote }}
- name: Telemetry__SegmentMaxAgeMinutes
  value: {{ .Values.telemetry.segmentMaxAgeMinutes | quote }}
- name: Telemetry__ArchiveZstdLevel
  value: {{ .Values.telemetry.archiveZstdLevel | quote }}
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
