FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# This Dockerfile exists for hosts configured with src/TennisBooking as the
# Docker build context. That context cannot see the solution root, so fetch the
# full repository inside the build stage.
ARG GIT_REPOSITORY=https://github.com/yaroslavshparuk/TennisBooking.git
ARG GIT_REF=master
RUN git clone --depth 1 --branch "$GIT_REF" "$GIT_REPOSITORY" .
RUN dotnet restore TennisBooking.slnx --disable-parallel
RUN dotnet publish src/TennisBooking/TennisBooking.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5000
ENTRYPOINT ["dotnet", "TennisBooking.dll"]
