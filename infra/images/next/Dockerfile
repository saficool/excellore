# syntax=docker/dockerfile:1.7
FROM node:22.22.0-bookworm-slim AS build

ENV PNPM_HOME=/pnpm
ENV PATH=$PNPM_HOME:$PATH
ARG APP_NAME
WORKDIR /workspace

RUN corepack enable && corepack prepare pnpm@10.34.6 --activate
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml turbo.json tsconfig.base.json ./
COPY apps/access/package.json ./apps/access/package.json
COPY apps/profile/package.json ./apps/profile/package.json
COPY apps/shell/package.json ./apps/shell/package.json
COPY packages/config/package.json ./packages/config/package.json
COPY packages/create-excellore-app/package.json ./packages/create-excellore-app/package.json
COPY packages/excellore-auth/package.json ./packages/excellore-auth/package.json
COPY packages/ui/package.json ./packages/ui/package.json
RUN pnpm install --frozen-lockfile
COPY apps ./apps
COPY packages ./packages
COPY infra/platform/app-catalog.yaml ./infra/platform/app-catalog.yaml
RUN test "$APP_NAME" = shell || test "$APP_NAME" = profile || test "$APP_NAME" = access
RUN pnpm --filter @excellore/auth build \
    && pnpm --filter @excellore/config build \
    && pnpm --filter @excellore/ui build \
    && pnpm --filter "@excellore/$APP_NAME" build
RUN mkdir -p "apps/$APP_NAME/public"

FROM node:22.22.0-bookworm-slim AS runtime
ARG APP_NAME
ENV NODE_ENV=production
ENV PORT=3000
ENV HOSTNAME=0.0.0.0
WORKDIR /app

RUN groupadd --gid 10001 excellore \
    && useradd --uid 10001 --gid excellore --no-create-home --shell /usr/sbin/nologin excellore
COPY --from=build --chown=excellore:excellore /workspace/apps/${APP_NAME}/.next/standalone ./
COPY --from=build --chown=excellore:excellore /workspace/apps/${APP_NAME}/.next/static ./apps/${APP_NAME}/.next/static
COPY --from=build --chown=excellore:excellore /workspace/apps/${APP_NAME}/public ./apps/${APP_NAME}/public
COPY --from=build --chown=excellore:excellore /workspace/infra/platform/app-catalog.yaml ./infra/platform/app-catalog.yaml
USER 10001:10001
WORKDIR /app/apps/${APP_NAME}
EXPOSE 3000
CMD ["node", "server.js"]
