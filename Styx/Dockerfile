FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Common/Common.csproj Common/
COPY Styx/Styx.csproj Styx/
RUN dotnet restore Styx/Styx.csproj

COPY Common/ Common/
COPY Styx/ Styx/
RUN dotnet publish Styx/Styx.csproj --configuration Release --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV LOCAL_PORT=5000
ENV ASPNETCORE_HTTP_PORTS=
EXPOSE 5000

ENTRYPOINT ["./Styx"]
