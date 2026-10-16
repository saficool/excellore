resource "kubernetes_namespace_v1" "excellore" {
  metadata {
    name = var.namespace
    labels = {
      "app.kubernetes.io/part-of"                  = "excellore"
      "pod-security.kubernetes.io/enforce"         = "restricted"
      "pod-security.kubernetes.io/enforce-version" = "latest"
      "pod-security.kubernetes.io/audit"           = "restricted"
      "pod-security.kubernetes.io/warn"            = "restricted"
    }
  }
}

resource "helm_release" "excellore" {
  name      = var.release_name
  namespace = kubernetes_namespace_v1.excellore.metadata[0].name
  chart     = var.chart_path
  values    = var.values

  atomic            = true
  cleanup_on_fail   = true
  dependency_update = false
  lint              = true
  timeout           = 900
  wait              = true
  wait_for_jobs     = true

  lifecycle {
    precondition {
      condition = alltrue([
        for document in var.values :
        !strcontains(document, ".example.invalid") &&
        !strcontains(document, "sha256:0000000000000000000000000000000000000000000000000000000000000000")
      ])
      error_message = "deployment values must replace example domains and all-zero image digests"
    }
  }

  depends_on = [kubernetes_namespace_v1.excellore]
}
