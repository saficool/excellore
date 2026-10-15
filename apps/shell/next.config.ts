import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  reactStrictMode: true,
  agentRules: false,
  transpilePackages: ['@excellore/ui'],
  allowedDevOrigins: ['*.excellore.localhost'],
  logging: { incomingRequests: false },
};

export default nextConfig;
