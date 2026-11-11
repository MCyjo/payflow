# One Dockerfile, one target per ASP.NET service. Build from the repository root:
#   docker build -f deploy/docker/Dockerfile --target order-service -t payflow/order-service .
#   docker build -f deploy/docker/Dockerfile --target payment-service -t payflow/payment-service .
#   docker build -f deploy/docker/Dockerfile --target provider-simulator -t payflow/provider-simulator .
# BuildKit only builds the stages a target depends on, so each build publishes just its own service.

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS restore
WORKDIR /src
COPY nuget.config global.json Directory.Build.props ./
COPY src/ src/

FROM restore AS publish-order
RUN dotnet publish src/OrderService/Api/PayFlow.OrderService.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM restore AS publish-payment
RUN dotnet publish src/PaymentService/Api/PayFlow.PaymentService.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM restore AS publish-simulator
RUN dotnet publish src/PaymentProviderSimulator/PayFlow.PaymentProviderSimulator.csproj -c Release -o /app /p:UseAppHost=false

# Chiseled image: no shell, no package manager, non-root user. Much smaller attack surface than a full base image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID

FROM runtime AS order-service
COPY --from=publish-order /app .
ENTRYPOINT ["dotnet", "PayFlow.OrderService.Api.dll"]

FROM runtime AS payment-service
COPY --from=publish-payment /app .
ENTRYPOINT ["dotnet", "PayFlow.PaymentService.Api.dll"]

FROM runtime AS provider-simulator
COPY --from=publish-simulator /app .
ENTRYPOINT ["dotnet", "PayFlow.PaymentProviderSimulator.dll"]
