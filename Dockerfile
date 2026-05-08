# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Ollama2LmStudioAdapter.Api/Ollama2LmStudioAdapter.Api.csproj", "src/Ollama2LmStudioAdapter.Api/"]
RUN dotnet restore "src/Ollama2LmStudioAdapter.Api/Ollama2LmStudioAdapter.Api.csproj"

COPY . .
WORKDIR "/src/src/Ollama2LmStudioAdapter.Api"
RUN dotnet build "Ollama2LmStudioAdapter.Api.csproj" -c Release -o /app/build

# ---- Publish stage ----
FROM build AS publish
RUN dotnet publish "Ollama2LmStudioAdapter.Api.csproj" -c Release -o /app/publish \
    --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=80
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "Ollama2LmStudioAdapter.Api.dll"]
