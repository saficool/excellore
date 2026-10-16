output "namespace" {
  description = "Namespace containing the Excellore platform."
  value       = kubernetes_namespace_v1.excellore.metadata[0].name
}

output "release_name" {
  description = "Installed Helm release name."
  value       = helm_release.excellore.name
}
