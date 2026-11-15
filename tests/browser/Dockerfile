FROM mcr.microsoft.com/playwright:v1.56.1-noble
WORKDIR /tests
COPY package*.json ./
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY registration.mjs .
CMD ["npm", "test"]
