FROM public.ecr.aws/lambda/dotnet:8 AS base

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1

# Install Node.js (Playwright dependency)
RUN apt-get update && \
    apt-get install -y wget gnupg ca-certificates && \
    wget -qO- https://deb.nodesource.com/setup_18.x | bash - && \
    apt-get install -y nodejs && \
    apt-get install -y libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libxkbcommon0 libxcomposite1 libxrandr2 libxdamage1 libxfixes3 libgbm1 libpango-1.0-0 libasound2 libxshmfence1 libx11-xcb1 fonts-liberation libappindicator3-1 libgtk-3-0 libdrm2 libxinerama1 libxslt1.1 && \
    npm install -g playwright@1.40.1 && \
    npx playwright install chromium && \
    rm -rf /var/lib/apt/lists/*

COPY thelastsupperticket.csproj ./
RUN dotnet restore ./thelastsupperticket.csproj -r linux-x64

COPY . .
RUN dotnet publish ./thelastsupperticket.csproj -c Release -r linux-x64 -o /out --no-restore

FROM base AS final
WORKDIR ${LAMBDA_TASK_ROOT}
RUN dnf install -y \
    nss \
    atk \
    at-spi2-atk \
    cups-libs \
    libdrm \
    libxkbcommon \
    libXcomposite \
    libXdamage \
    libXfixes \
    libXrandr \
    mesa-libgbm \
    pango \
    alsa-lib \
    gtk3 \
    libX11-xcb && \
    dnf clean all
COPY --from=build /out ${LAMBDA_TASK_ROOT}
COPY --from=build /ms-playwright /ms-playwright
RUN if [ -d "${LAMBDA_TASK_ROOT}/.playwright" ]; then chmod -R a+rx "${LAMBDA_TASK_ROOT}/.playwright"; fi
RUN if [ -d "/ms-playwright" ]; then chmod -R a+rx "/ms-playwright"; fi
ENV PATH="$PATH:/root/.dotnet/tools"
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
CMD ["thelastsupperticket::TheLastSupperTicket.Function::FunctionHandler"]