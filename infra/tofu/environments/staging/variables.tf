variable "kubeconfig_path" {
  description = "Path to a short-lived kubeconfig supplied by the deployment runner."
  type        = string
}

variable "values_file" {
  description = "Path to pipeline-rendered, non-secret Helm values with immutable image digests."
  type        = string
}
