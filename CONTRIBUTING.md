# Contributing to BARS vatSys Plugin

Thank you for your interest in contributing to the BARS vatSys Plugin! This guide will help you get started with contributing to this C# plugin for vatSys.

## Getting Started

### Prerequisites

- Visual Studio 2019 or higher (Community Edition is fine)
- .NET Framework 4.7.2 or higher
- vatSys installed and configured
- Git
- Basic knowledge of C# and Windows Forms

### Development Setup

1. **Fork and Clone**

   ```cmd
   git clone https://github.com/stopbars/vatSys-Plugin.git
   cd vatSys-Plugin
   ```

2. **Open Solution**

   Open `BARS-Plugin-V2.sln` in Visual Studio.

3. **Set Build Configuration**

   - Set the build configuration to `Release` and platform to `x86` for final builds
   - Use `Debug|x86` for development and testing

4. **Build the Plugin**

   ```cmd
   msbuild BARS-Plugin-V2.sln /p:Configuration=Release /p:Platform=x86
   ```

   The compiled plugin will be available in `bin\x86\Release\BARS.dll`.

5. **Available Build Configurations**

   - `Debug|AnyCPU` - Development build with debug symbols
   - `Release|AnyCPU` - Production build optimized
   - `Debug|x86` - Development build for x86 platform (recommended for vatSys)
   - `Release|x86` - Production build for x86 platform (recommended for vatSys)

## Development Guidelines

### Code Style

- Follow C# naming conventions (PascalCase for classes, methods, properties)
- Use camelCase for private fields and local variables
- Add XML documentation comments for public methods and classes
- Use meaningful variable and method names
- Keep methods focused and avoid overly long functions
- Follow the existing project structure and organization
- Use proper exception handling and logging

### Code Guidelines

- Use the existing Logger class for debugging and error reporting
- Follow the MEF composition pattern for plugin architecture
- Implement proper disposal patterns for Windows Forms
- Use async/await patterns for network operations where appropriate
- Handle vatSys API calls with proper error checking
- Test thoroughly with vatSys before submitting changes

### vatSys Integration

- Understand the vatSys Plugin interface (`IPlugin`)
- Use vatSys APIs appropriately and handle connection states
- Test plugin behavior during vatSys startup, connection, and shutdown
- Ensure compatibility with different vatSys versions

## Contribution Process

### 1. Find or Create an Issue

- Browse existing issues for bug fixes or feature requests
- Create a new issue for significant changes
- Discuss the approach before starting work

### 2. Create a Feature Branch

```cmd
git checkout -b feature/your-feature-name
REM or
git checkout -b fix/your-bug-fix
```

### 3. Make Your Changes

- Write clean, well-documented C# code
- Test your changes thoroughly with vatSys
- Update documentation if necessary
- Ensure builds complete without errors or warnings

### 4. Commit Your Changes

```cmd
git add .
git commit -m "Add brief description of your changes"
```

Use clear, descriptive commit messages:

- `feat: add new airport profile management`
- `fix: resolve controller window display issue`
- `docs: update plugin installation instructions`
- `refactor: improve network connection handling`

### 5. Push and Create Pull Request

```cmd
git push origin feature/your-feature-name
```

Create a pull request with:

- Clear description of changes
- Reference to related issues
- Screenshots for UI changes (if applicable)
- Testing instructions for new features
- Confirmation that plugin builds and loads in vatSys

## Testing

### Local Testing

1. Build the plugin: `msbuild BARS-Plugin-V2.sln /p:Configuration=Release /p:Platform=x86`
2. Copy `BARS.dll` to your vatSys plugins directory
3. Start vatSys and verify the plugin loads without errors
4. Test all affected functionality thoroughly
5. Check the BARS log files for any errors: `%LOCALAPPDATA%\BARS\BARS-V2.log`

### Build Testing

1. Clean the solution: `Build > Clean Solution` in Visual Studio
2. Rebuild in Release mode: `Build > Rebuild Solution`
3. Verify no compiler warnings or errors
4. Test plugin loading in a fresh vatSys installation

### Integration Testing

- Test with different vatSys configurations and scenarios
- Verify plugin behavior during connection/disconnection events
- Test UI responsiveness and proper form handling
- Ensure proper resource cleanup and disposal

## Common Issues

### Build Errors

- Ensure all NuGet packages are restored: `Tools > NuGet Package Manager > Package Manager Console` then run `Update-Package -reinstall`
- Check that .NET Framework 4.7.2 is installed
- Verify vatSys SDK references are correct
- Clean and rebuild the solution if you encounter cache issues

### Plugin Loading Issues

- Ensure the plugin is built for x86 architecture (vatSys requirement)
- Check that all dependencies are present in the output directory
- Verify the plugin implements the IPlugin interface correctly
- Check vatSys logs for loading errors

### Runtime Errors

- Review the BARS log file: `%LOCALAPPDATA%\BARS\BARS-V2.log`
- Use Visual Studio debugger when possible
- Test in different vatSys connection states
- Verify proper exception handling in network operations

## Getting Help

- **Discord**: [Join the BARS Discord server](https://discord.gg/7EhmtwKWzs) for real-time help
- **GitHub Issues**: [Create an issue](https://github.com/stopbars/vatSys-Plugin/issues/new) for bugs or feature requests
- **Code Review**: Ask for review on complex changes
- **vatSys Documentation**: Refer to vatSys plugin development documentation

## Recognition

Contributors are recognized in:

- Release notes for significant contributions
- Plugin credits and documentation

Thank you for helping make the BARS vatSys Plugin better for the virtual air traffic control community!
