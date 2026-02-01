#include "mainwindow.h"
#include "buildoutput.h"
#include "projecttree.h"

#include <QMenuBar>
#include <QToolBar>
#include <QStatusBar>
#include <QSplitter>
#include <QComboBox>
#include <QProgressBar>
#include <QLabel>
#include <QFileDialog>
#include <QMessageBox>
#include <QDir>
#include <QStandardPaths>
#include <QCoreApplication>

MainWindow::MainWindow(QWidget *parent)
    : QMainWindow(parent)
    , m_buildProcess(new QProcess(this))
    , m_settings("Omen", "OmenUI")
{
    setWindowTitle("Omen Build System");
    resize(1200, 800);

    setupUi();
    setupMenus();
    setupToolbar();
    setupConnections();
    applyDarkTheme();

    // Load saved CLI path first
    m_cliPath = m_settings.value("cliPath").toString();
    if (m_cliPath.isEmpty() || !QFile::exists(m_cliPath)) {
        m_cliPath = findOmenCli();
    }

    // Load last project
    QString lastProject = m_settings.value("lastProject").toString();
    if (!lastProject.isEmpty() && QDir(lastProject).exists()) {
        m_projectPath = lastProject;
        m_projectTree->loadProject(lastProject);
        m_statusLabel->setText("Project: " + QDir(lastProject).dirName());
    }

    updateBuildState(false);

    // Show CLI status
    if (m_cliPath.isEmpty()) {
        m_buildOutput->appendWarning("Omen CLI not found. Go to File > Settings to set the CLI path.");
    } else {
        m_buildOutput->appendInfo("CLI: " + m_cliPath);
    }
}

MainWindow::~MainWindow()
{
    if (m_buildProcess->state() != QProcess::NotRunning) {
        m_buildProcess->kill();
        m_buildProcess->waitForFinished(3000);
    }
}

void MainWindow::setupUi()
{
    // Central splitter
    m_splitter = new QSplitter(Qt::Horizontal, this);
    setCentralWidget(m_splitter);

    // Project tree
    m_projectTree = new ProjectTree(this);
    m_projectTree->setMinimumWidth(250);
    m_splitter->addWidget(m_projectTree);

    // Build output
    m_buildOutput = new BuildOutput(this);
    m_splitter->addWidget(m_buildOutput);

    m_splitter->setStretchFactor(0, 1);
    m_splitter->setStretchFactor(1, 3);

    // Status bar
    m_statusLabel = new QLabel("Ready");
    statusBar()->addWidget(m_statusLabel, 1);

    m_progressBar = new QProgressBar();
    m_progressBar->setMaximumWidth(200);
    m_progressBar->setVisible(false);
    statusBar()->addPermanentWidget(m_progressBar);
}

void MainWindow::setupMenus()
{
    // File menu
    QMenu *fileMenu = menuBar()->addMenu("&File");

    m_openAction = fileMenu->addAction("&Open Project...");
    m_openAction->setShortcut(QKeySequence::Open);

    fileMenu->addAction("&Close Project", this, &MainWindow::closeProject);
    fileMenu->addSeparator();

    m_settingsAction = fileMenu->addAction("&Settings...");
    m_settingsAction->setShortcut(QKeySequence("Ctrl+,"));

    fileMenu->addSeparator();
    fileMenu->addAction("E&xit", this, &QMainWindow::close, QKeySequence::Quit);

    // Build menu
    QMenu *buildMenu = menuBar()->addMenu("&Build");

    m_buildAction = buildMenu->addAction("&Build");
    m_buildAction->setShortcut(QKeySequence("F7"));

    m_rebuildAction = buildMenu->addAction("&Rebuild");
    m_rebuildAction->setShortcut(QKeySequence("Ctrl+Shift+B"));

    m_cleanAction = buildMenu->addAction("&Clean");

    buildMenu->addSeparator();

    m_cancelAction = buildMenu->addAction("C&ancel");
    m_cancelAction->setShortcut(QKeySequence("Ctrl+Break"));

    // Project menu
    QMenu *projectMenu = menuBar()->addMenu("&Project");

    QMenu *generateMenu = projectMenu->addMenu("&Generate Project Files");

    m_generateVS2022Action = generateMenu->addAction("Visual Studio 2022");
    m_generateVS2022Action->setData("vs2022");

    m_generateVS2019Action = generateMenu->addAction("Visual Studio 2019");
    m_generateVS2019Action->setData("vs2019");

    generateMenu->addSeparator();

    m_generateVSCodeAction = generateMenu->addAction("Visual Studio Code");
    m_generateVSCodeAction->setData("vscode");

    m_generateCMakeAction = generateMenu->addAction("CMake");
    m_generateCMakeAction->setData("cmake");

    // Help menu
    QMenu *helpMenu = menuBar()->addMenu("&Help");
    helpMenu->addAction("&About Omen", this, &MainWindow::showAbout);
}

void MainWindow::setupToolbar()
{
    m_toolbar = addToolBar("Main");
    m_toolbar->setMovable(false);
    m_toolbar->setIconSize(QSize(20, 20));

    m_toolbar->addAction(m_openAction);
    m_toolbar->addSeparator();

    // Platform combo
    m_platformCombo = new QComboBox();
    m_platformCombo->addItems({"Windows", "Linux", "MacOS", "FreeBSD"});
    m_toolbar->addWidget(new QLabel(" Platform: "));
    m_toolbar->addWidget(m_platformCombo);

    // Config combo
    m_configCombo = new QComboBox();
    m_configCombo->addItems({"Debug", "Release", "Shipping"});
    m_toolbar->addWidget(new QLabel(" Config: "));
    m_toolbar->addWidget(m_configCombo);

    m_toolbar->addSeparator();
    m_toolbar->addAction(m_buildAction);
    m_toolbar->addAction(m_rebuildAction);
    m_toolbar->addAction(m_cleanAction);
    m_toolbar->addSeparator();
    m_toolbar->addAction(m_cancelAction);
}

void MainWindow::setupConnections()
{
    connect(m_openAction, &QAction::triggered, this, &MainWindow::openProject);
    connect(m_settingsAction, &QAction::triggered, this, &MainWindow::showSettings);
    connect(m_buildAction, &QAction::triggered, this, &MainWindow::startBuild);
    connect(m_rebuildAction, &QAction::triggered, this, &MainWindow::startRebuild);
    connect(m_cleanAction, &QAction::triggered, this, &MainWindow::startClean);
    connect(m_cancelAction, &QAction::triggered, this, &MainWindow::cancelBuild);

    // Generate solution actions
    connect(m_generateVS2022Action, &QAction::triggered, this, &MainWindow::generateSolution);
    connect(m_generateVS2019Action, &QAction::triggered, this, &MainWindow::generateSolution);
    connect(m_generateVSCodeAction, &QAction::triggered, this, &MainWindow::generateSolution);
    connect(m_generateCMakeAction, &QAction::triggered, this, &MainWindow::generateSolution);

    connect(m_buildProcess, QOverload<int, QProcess::ExitStatus>::of(&QProcess::finished),
            this, &MainWindow::onBuildFinished);
    connect(m_buildProcess, &QProcess::readyReadStandardOutput, this, &MainWindow::onBuildOutput);
    connect(m_buildProcess, &QProcess::readyReadStandardError, this, &MainWindow::onBuildError);

    // Handle process errors
    connect(m_buildProcess, &QProcess::errorOccurred, this, [this](QProcess::ProcessError error) {
        updateBuildState(false);
        QString msg;
        switch (error) {
            case QProcess::FailedToStart:
                msg = "Failed to start CLI. Check that the path is correct and the file is executable.";
                break;
            case QProcess::Crashed:
                msg = "CLI process crashed.";
                break;
            case QProcess::Timedout:
                msg = "CLI process timed out.";
                break;
            default:
                msg = "Unknown process error.";
                break;
        }
        m_buildOutput->appendError(msg);
        m_buildOutput->appendError("CLI path: " + m_cliPath);
    });
}

void MainWindow::applyDarkTheme()
{
    // GitHub-inspired dark theme
    setStyleSheet(R"(
        QMainWindow, QDialog {
            background-color: #161B22;
            color: #C9D1D9;
        }
        QMenuBar {
            background-color: #161B22;
            color: #C9D1D9;
            border-bottom: 1px solid #30363D;
        }
        QMenuBar::item:selected {
            background-color: #30363D;
        }
        QMenu {
            background-color: #21262D;
            color: #C9D1D9;
            border: 1px solid #30363D;
        }
        QMenu::item:selected {
            background-color: #30363D;
        }
        QToolBar {
            background-color: #161B22;
            border-bottom: 1px solid #30363D;
            spacing: 6px;
            padding: 4px;
        }
        QToolButton {
            background-color: transparent;
            color: #C9D1D9;
            border: none;
            border-radius: 4px;
            padding: 6px 12px;
        }
        QToolButton:hover {
            background-color: #30363D;
        }
        QToolButton:pressed {
            background-color: #484F58;
        }
        QComboBox {
            background-color: #21262D;
            color: #C9D1D9;
            border: 1px solid #30363D;
            border-radius: 4px;
            padding: 4px 8px;
            min-width: 100px;
        }
        QComboBox:hover {
            border-color: #8B949E;
        }
        QComboBox::drop-down {
            border: none;
            width: 20px;
        }
        QComboBox QAbstractItemView {
            background-color: #21262D;
            color: #C9D1D9;
            selection-background-color: #30363D;
        }
        QTreeWidget {
            background-color: #0D1117;
            color: #C9D1D9;
            border: none;
            outline: none;
        }
        QTreeWidget::item {
            padding: 4px;
        }
        QTreeWidget::item:hover {
            background-color: #21262D;
        }
        QTreeWidget::item:selected {
            background-color: #1F6FEB;
        }
        QPlainTextEdit {
            background-color: #0D1117;
            color: #C9D1D9;
            border: none;
            font-family: 'Cascadia Code', 'Consolas', 'Courier New', monospace;
            font-size: 12px;
        }
        QSplitter::handle {
            background-color: #30363D;
            width: 1px;
        }
        QStatusBar {
            background-color: #161B22;
            color: #8B949E;
            border-top: 1px solid #30363D;
        }
        QProgressBar {
            background-color: #21262D;
            border: none;
            border-radius: 4px;
            height: 8px;
            text-align: center;
        }
        QProgressBar::chunk {
            background-color: #F97316;
            border-radius: 4px;
        }
        QLabel {
            color: #8B949E;
        }
        QScrollBar:vertical {
            background-color: #0D1117;
            width: 12px;
            border: none;
        }
        QScrollBar::handle:vertical {
            background-color: #30363D;
            border-radius: 6px;
            min-height: 20px;
        }
        QScrollBar::handle:vertical:hover {
            background-color: #484F58;
        }
        QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
            height: 0px;
        }
    )");
}

void MainWindow::openProject()
{
    QString dir = QFileDialog::getExistingDirectory(
        this, "Open Omen Project",
        m_projectPath.isEmpty() ? QDir::homePath() : m_projectPath
    );

    if (!dir.isEmpty()) {
        m_projectPath = dir;
        m_settings.setValue("lastProject", dir);
        m_projectTree->loadProject(dir);
        m_statusLabel->setText("Project: " + QDir(dir).dirName());
        m_buildOutput->clearOutput();
        m_buildOutput->appendInfo("Opened project: " + dir);
        updateBuildState(false);
    }
}

void MainWindow::closeProject()
{
    m_projectPath.clear();
    m_projectTree->clearProject();
    m_buildOutput->clearOutput();
    m_statusLabel->setText("Ready");
    updateBuildState(false);
}

void MainWindow::showSettings()
{
    QString path = QFileDialog::getOpenFileName(
        this, "Select Omen CLI",
        m_cliPath.isEmpty() ? QDir::homePath() : QFileInfo(m_cliPath).absolutePath(),
#ifdef Q_OS_WIN
        "Executable (*.exe *.dll);;All Files (*.*)"
#else
        "All Files (*)"
#endif
    );

    if (!path.isEmpty()) {
        m_cliPath = path;
        m_settings.setValue("cliPath", path);
        m_buildOutput->clearOutput();
        m_buildOutput->appendSuccess("CLI path set to: " + path);

        // Verify it exists
        if (QFile::exists(path)) {
            m_buildOutput->appendInfo("CLI file verified.");
        } else {
            m_buildOutput->appendError("Warning: File does not exist!");
        }

        updateBuildState(false);
    }
}

void MainWindow::startBuild()
{
    runOmenCommand({"build", "-c", m_configCombo->currentText(), "-p", m_platformCombo->currentText()});
}

void MainWindow::startRebuild()
{
    runOmenCommand({"rebuild", "-c", m_configCombo->currentText(), "-p", m_platformCombo->currentText()});
}

void MainWindow::startClean()
{
    runOmenCommand({"clean"});
}

void MainWindow::cancelBuild()
{
    if (m_buildProcess->state() != QProcess::NotRunning) {
        m_buildProcess->kill();
        m_buildOutput->appendWarning("Build cancelled by user");
        updateBuildState(false);
    }
}

void MainWindow::generateSolution()
{
    QAction *action = qobject_cast<QAction*>(sender());
    if (!action) return;

    QString ide = action->data().toString();
    if (ide.isEmpty()) return;

    m_buildOutput->appendInfo("Generating " + action->text() + " project files...");
    runOmenCommand({"generate", "project", "--ide", ide});
}

void MainWindow::runOmenCommand(const QStringList &args)
{
    if (m_projectPath.isEmpty()) {
        QMessageBox::warning(this, "No Project", "Please open a project first.");
        return;
    }

    if (m_cliPath.isEmpty()) {
        m_cliPath = findOmenCli();
    }

    if (m_cliPath.isEmpty()) {
        QMessageBox::warning(this, "CLI Not Found",
            "Omen CLI not found.\n\nPlease go to File > Settings to set the CLI path.\n\n"
            "The CLI should be one of:\n"
            "- omen.exe (standalone)\n"
            "- Omen.CLI.dll (run via dotnet)");
        return;
    }

    if (!QFile::exists(m_cliPath)) {
        QMessageBox::warning(this, "CLI Not Found",
            QString("CLI file not found at:\n%1\n\nPlease go to File > Settings to update the path.").arg(m_cliPath));
        m_cliPath.clear();
        m_settings.remove("cliPath");
        return;
    }

    m_buildOutput->clearOutput();

    // Determine how to run the CLI
    QString program;
    QStringList fullArgs;

    if (m_cliPath.endsWith(".dll", Qt::CaseInsensitive)) {
        // It's a .NET DLL - run via dotnet
        program = "dotnet";
        fullArgs << m_cliPath << args;
        m_buildOutput->appendInfo("$ dotnet " + m_cliPath + " " + args.join(" "));
    } else {
        // It's an executable
        program = m_cliPath;
        fullArgs = args;
        m_buildOutput->appendInfo("$ " + m_cliPath + " " + args.join(" "));
    }

    m_buildOutput->appendInfo("Working directory: " + m_projectPath);

    m_buildProcess->setWorkingDirectory(m_projectPath);
    m_buildProcess->start(program, fullArgs);

    updateBuildState(true);
}

QString MainWindow::findOmenCli()
{
    // Check settings first
    QString saved = m_settings.value("cliPath").toString();
    if (!saved.isEmpty() && QFile::exists(saved)) {
        return saved;
    }

    // Check common locations
    QStringList paths;
#ifdef Q_OS_WIN
    paths << QDir::homePath() + "/.dotnet/tools/omen.exe"
          << "C:/Program Files/Omen/omen.exe"
          << QCoreApplication::applicationDirPath() + "/omen.exe"
          << QCoreApplication::applicationDirPath() + "/Omen.CLI.exe"
          << QCoreApplication::applicationDirPath() + "/Omen.CLI.dll";
#else
    paths << QDir::homePath() + "/.dotnet/tools/omen"
          << "/usr/local/bin/omen"
          << "/usr/bin/omen"
          << QCoreApplication::applicationDirPath() + "/omen"
          << QCoreApplication::applicationDirPath() + "/Omen.CLI.dll";
#endif

    for (const QString &path : paths) {
        if (QFile::exists(path)) {
            m_settings.setValue("cliPath", path);
            return path;
        }
    }

    return QString();
}

void MainWindow::onBuildFinished(int exitCode, QProcess::ExitStatus status)
{
    updateBuildState(false);

    if (status == QProcess::CrashExit) {
        m_buildOutput->appendError("Build process crashed!");
    } else if (exitCode == 0) {
        m_buildOutput->appendSuccess("Build completed successfully!");
    } else {
        m_buildOutput->appendError(QString("Build failed with exit code %1").arg(exitCode));
    }
}

void MainWindow::onBuildOutput()
{
    QString output = QString::fromUtf8(m_buildProcess->readAllStandardOutput());
    m_buildOutput->appendOutput(output);
}

void MainWindow::onBuildError()
{
    QString error = QString::fromUtf8(m_buildProcess->readAllStandardError());
    m_buildOutput->appendError(error);
}

void MainWindow::updateBuildState(bool building)
{
    bool hasProject = !m_projectPath.isEmpty();
    bool hasCli = !m_cliPath.isEmpty();
    bool canBuild = !building && hasProject && hasCli;

    m_buildAction->setEnabled(canBuild);
    m_rebuildAction->setEnabled(canBuild);
    m_cleanAction->setEnabled(canBuild);
    m_cancelAction->setEnabled(building);
    m_progressBar->setVisible(building);

    // Enable generate actions
    m_generateVS2022Action->setEnabled(canBuild);
    m_generateVS2019Action->setEnabled(canBuild);
    m_generateVSCodeAction->setEnabled(canBuild);
    m_generateCMakeAction->setEnabled(canBuild);

    if (building) {
        m_progressBar->setRange(0, 0);  // Indeterminate
        m_statusLabel->setText("Building...");
    } else if (!hasProject) {
        m_statusLabel->setText("No project open");
    } else if (!hasCli) {
        m_statusLabel->setText("CLI not configured - go to File > Settings");
    } else {
        m_statusLabel->setText("Project: " + QDir(m_projectPath).dirName());
    }
}

void MainWindow::showAbout()
{
    QMessageBox::about(this, "About Omen",
        "<h2>Omen Build System</h2>"
        "<p>Version 1.0.0</p>"
        "<p>A pure C# build system inspired by Unreal Build Tool.</p>"
        "<p>Cross-platform GUI built with Qt.</p>"
    );
}
