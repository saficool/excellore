# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0.103-noble AS build
ARG PROJECT
WORKDIR /src
COPY Excellore.sln ./
COPY services ./services
COPY shared ./shared
RUN test -n "$PROJECT" && dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" --configuration Release --no-restore --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build --chown=1654:1654 /out ./
USER 1654:1654
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet"]
