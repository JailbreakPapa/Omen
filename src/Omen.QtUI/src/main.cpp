#include <QApplication>
#include <QStyleFactory>
#include "mainwindow.h"

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    
    app.setApplicationName("Omen Build System");
    app.setOrganizationName("Omen");
    app.setApplicationVersion("1.0.0");
    
    // Use Fusion style for consistent cross-platform look
    app.setStyle(QStyleFactory::create("Fusion"));
    
    MainWindow window;
    window.show();
    
    return app.exec();
}
