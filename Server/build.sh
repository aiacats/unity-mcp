#!/bin/bash

# Claude Code MCP Unity Server Build Script
# This script installs dependencies and prepares the server for use

echo "Building Claude Code MCP Unity Server..."

# Check if npm is available
if ! command -v npm &> /dev/null; then
    echo "Error: npm is not installed or not in PATH"
    echo "Please install Node.js and npm first"
    exit 1
fi

# Install dependencies
echo "Installing dependencies..."
npm install

# Create build directory
mkdir -p build

# Copy files to build directory
echo "Copying files to build directory..."
cp index.js build/
cp package.json build/

echo "Build complete!"
echo "Server is ready to use at: $(pwd)/build/index.js"