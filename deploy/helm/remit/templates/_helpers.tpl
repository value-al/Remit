{{- define "remit.labels" -}}
app.kubernetes.io/part-of: remit
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end }}

{{/* Environment shared by the two services: configuration by env var, secrets from the CSI-synced Secret. */}}
{{- define "remit.commonEnv" -}}
- name: ASPNETCORE_ENVIRONMENT
  value: Production
- name: Database__MigrateOnStartup
  value: "false"
- name: RabbitMq__Uri
  value: amqp://{{ .Values.rabbitmq.username }}:{{ .Values.rabbitmq.password }}@rabbitmq:5672
- name: RabbitMq__Exchange
  value: remit
- name: Otel__Endpoint
  value: http://jaeger:4317
{{- end }}
