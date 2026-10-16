provider "kubernetes" {
  config_path = var.kubeconfig_path
}

provider "helm" {
  kubernetes = {
    config_path = var.kubeconfig_path
  }
}

module "excellore" {
  source = "../../modules/excellore-platform"

  namespace  = "excellore-staging"
  chart_path = abspath("${path.module}/../../../k8s/excellore")
  values     = [file(var.values_file)]
}
