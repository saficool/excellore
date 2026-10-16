variable "namespace" {
  description = "Dedicated namespace for the Excellore release."
  type        = string
  default     = "excellore"

  validation {
    condition     = can(regex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", var.namespace))
    error_message = "namespace must be a valid DNS label"
  }
}

variable "chart_path" {
  description = "Absolute path to the reviewed Excellore Helm chart."
  type        = string
}

variable "values" {
  description = "Fully rendered, non-secret Helm values. Secret values stay in the external secret store."
  type        = list(string)
}

variable "release_name" {
  description = "Helm release name."
  type        = string
  default     = "excellore"
}
