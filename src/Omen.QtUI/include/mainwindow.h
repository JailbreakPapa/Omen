#ifndef MAINWINDOW_H
#define MAINWINDOW_H

#include <QMainWindow>
#include <QProcess>
#include <QSettings>

class BuildOutput;
class ProjectTree;
class QSplitter;
class QToolBar;
class QComboBox;
class QProgressBar;
class QLabel;
class QMenu;

class MainWindow : public QMainWindow
{
    Q_OBJECT

public:
    explicit MainWindow(QWidget *parent = nullptr);
    ~MainWindow();

private slots:
    void openProject();
    void closeProject();
    void showSettings();
    void startBuild();
    void startRebuild();
    void startClean();
    void cancelBuild();
    void generateSolution();
    void onBuildFinished(int exitCode, QProcess::ExitStatus status);
    void onBuildOutput();
    void onBuildError();
    void showAbout();

private:
    void setupUi();
    void setupMenus();
    void setupToolbar();
    void setupConnections();
    void applyDarkTheme();
    void updateBuildState(bool building);
    void runOmenCommand(const QStringList &args);
    QString findOmenCli();

    // UI Components
    QSplitter *m_splitter;
    ProjectTree *m_projectTree;
    BuildOutput *m_buildOutput;
    QToolBar *m_toolbar;
    QComboBox *m_configCombo;
    QComboBox *m_platformCombo;
    QProgressBar *m_progressBar;
    QLabel *m_statusLabel;

    // Actions
    QAction *m_openAction;
    QAction *m_buildAction;
    QAction *m_rebuildAction;
    QAction *m_cleanAction;
    QAction *m_cancelAction;
    QAction *m_settingsAction;
    QAction *m_generateVS2022Action;
    QAction *m_generateVS2019Action;
    QAction *m_generateVSCodeAction;
    QAction *m_generateCMakeAction;

    // State
    QProcess *m_buildProcess;
    QString m_projectPath;
    QString m_cliPath;
    QSettings m_settings;
};

#endif // MAINWINDOW_H
